// Minimal, dependency-free markdown -> HTML renderer with HTML escaping.
// Good enough for chat: fenced code, inline code, bold/italic, headings, lists, links, hr, blockquote.
(function (global) {
  function esc(s) {
    return s
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  // True when a backtick span looks like a file path that could be opened in the editor.
  // Requires a path separator and a file extension in the last component; strips optional :line suffix.
  function looksLikePath(s) {
    if (!s || s.length > 300) return false;
    if (/^https?:\/\//i.test(s)) return false;
    var p = s.replace(/:\d+$/, '');
    if (!/[\/\\]/.test(p)) return false;
    var last = p.split(/[\/\\]/).pop();
    return last ? /\.[a-z0-9]{1,6}$/i.test(last) : false;
  }

  function inline(s) {
    // inline code first, protect from other rules
    const codes = [];
    s = s.replace(/`([^`]+)`/g, function (_, c) {
      var cls = looksLikePath(c) ? ' class="fp"' : '';
      codes.push('<code' + cls + '>' + esc(c) + '</code>');
      return "\u0000" + (codes.length - 1) + "\u0000";
    });
    s = esc(s);
    // links [text](url) — only http(s)/mailto; drop unsafe schemes (javascript:, data:, file:…)
    s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, function (_, text, url) {
      if (!/^(https?:|mailto:)/i.test(url)) return text;
      var safe = url.replace(/"/g, "%22");
      return '<a href="' + safe + '" target="_blank" rel="noreferrer noopener">' + text + '</a>';
    });
    // bold then italic
    s = s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    s = s.replace(/(^|[^*])\*([^*]+)\*/g, "$1<em>$2</em>");
    s = s.replace(/_([^_]+)_/g, "<em>$1</em>");
    // restore inline code
    s = s.replace(/\u0000(\d+)\u0000/g, function (_, i) { return codes[+i]; });
    return s;
  }

  // Keyword sets for the languages we recognise.
  const KW = {
    cpp: new Set(['auto','bool','break','case','catch','char','class','const','constexpr',
      'continue','default','delete','do','double','else','enum','explicit','extern',
      'false','float','for','friend','goto','if','inline','int','long','mutable',
      'namespace','new','nullptr','operator','private','protected','public','register',
      'return','short','signed','sizeof','static','struct','switch','template','this',
      'throw','true','try','typedef','typename','union','unsigned','using','virtual',
      'void','volatile','while']),
    cs: new Set(['abstract','as','async','await','base','bool','break','byte','case',
      'catch','char','checked','class','const','continue','decimal','default','delegate',
      'do','double','else','enum','event','explicit','extern','false','finally','fixed',
      'float','for','foreach','goto','if','implicit','in','int','interface','internal',
      'is','lock','long','namespace','new','null','object','operator','out','override',
      'params','private','protected','public','readonly','ref','return','sbyte','sealed',
      'short','sizeof','stackalloc','static','string','struct','switch','this','throw',
      'true','try','typeof','uint','ulong','unchecked','unsafe','ushort','using','var',
      'virtual','void','volatile','while','yield','dynamic','nameof','when']),
    js: new Set(['async','await','break','case','catch','class','const','continue',
      'debugger','default','delete','do','else','export','extends','false','finally',
      'for','from','function','if','import','in','instanceof','let','new','null','of',
      'return','static','super','switch','this','throw','true','try','typeof','undefined',
      'var','void','while','with','yield']),
    css: new Set(['none','auto','inherit','initial','unset','revert',
      'flex','grid','block','inline','inline-block','inline-flex','inline-grid','contents',
      'relative','absolute','fixed','static','sticky',
      'bold','normal','italic','oblique',
      'hidden','visible','transparent','solid','dashed','dotted','double','groove','ridge',
      'center','left','right','top','bottom','middle','baseline','stretch',
      'nowrap','wrap','wrap-reverse','row','column','row-reverse','column-reverse',
      'space-between','space-around','space-evenly','flex-start','flex-end',
      'contain','cover','fill','no-repeat','repeat','repeat-x','repeat-y',
      'pointer','default','text','crosshair','not-allowed','grab','grabbing',
      'uppercase','lowercase','capitalize',
      'underline','line-through','overline',
      'scroll','hidden','clip','ellipsis','collapse','separate',
      'ease','linear','ease-in','ease-out','ease-in-out','forwards','backwards','both',
      'border-box','content-box','padding-box']),
    json: new Set(['true','false','null']),
    sql: new Set(['SELECT','FROM','WHERE','INSERT','INTO','VALUES','UPDATE','SET','DELETE',
      'CREATE','DROP','ALTER','TABLE','VIEW','INDEX','PROCEDURE','FUNCTION','TRIGGER',
      'JOIN','INNER','LEFT','RIGHT','FULL','OUTER','CROSS','ON','AS','AND','OR','NOT',
      'NULL','IS','IN','BETWEEN','LIKE','EXISTS','ANY','ALL','UNION','INTERSECT','EXCEPT',
      'ORDER','BY','GROUP','HAVING','DISTINCT','TOP','LIMIT','OFFSET','FETCH','NEXT',
      'ROWS','ONLY','CASE','WHEN','THEN','ELSE','END','IF','BEGIN','COMMIT','ROLLBACK',
      'TRANSACTION','DECLARE','EXEC','EXECUTE','PRINT','RETURN','GO','USE','DATABASE',
      'PRIMARY','FOREIGN','KEY','REFERENCES','CONSTRAINT','DEFAULT','UNIQUE','CHECK',
      'CASCADE','WITH','NOLOCK','NOCOUNT','OUTPUT','OVER','PARTITION','ROW_NUMBER',
      'RANK','DENSE_RANK','COUNT','SUM','AVG','MIN','MAX','COALESCE','ISNULL',
      'CAST','CONVERT','LEN','SUBSTRING','REPLACE','GETDATE','NEWID','SCOPE_IDENTITY',
      'INT','BIGINT','SMALLINT','TINYINT','DECIMAL','NUMERIC','FLOAT','REAL','MONEY',
      'VARCHAR','NVARCHAR','CHAR','NCHAR','TEXT','NTEXT','BIT','DATETIME','DATE','TIME',
      'UNIQUEIDENTIFIER','VARBINARY','IMAGE']),
    py: new Set(['False','None','True','and','as','assert','async','await','break','class',
      'continue','def','del','elif','else','except','finally','for','from','global','if',
      'import','in','is','lambda','nonlocal','not','or','pass','raise','return','try',
      'while','with','yield'])
  };

  function langFamily(lang) {
    var l = (lang || '').toLowerCase();
    if (l === 'cpp' || l === 'c++' || l === 'c' || l === 'h' || l === 'cxx' || l === 'cc') return 'cpp';
    if (l === 'csharp' || l === 'cs' || l === 'c#') return 'cs';
    if (l === 'js' || l === 'javascript' || l === 'ts' || l === 'typescript' || l === 'jsx' || l === 'tsx') return 'js';
    if (l === 'css' || l === 'scss' || l === 'less') return 'css';
    if (l === 'json' || l === 'jsonc') return 'json';
    if (l === 'sql' || l === 'tsql' || l === 'plsql' || l === 'mysql') return 'sql';
    if (l === 'py' || l === 'python' || l === 'python3') return 'py';
    if (l === 'xml' || l === 'html' || l === 'htm' || l === 'xhtml' || l === 'svg' || l === 'xaml') return 'xml';
    if (l === 'md' || l === 'markdown') return 'md';
    return null;
  }

  // Markdown inline: bold, inline-code, links — called from highlightMarkdown line processor.
  function mdInline(s) {
    var out = ''; var i = 0; var n = s.length;
    while (i < n) {
      // Inline code `...`
      if (s[i] === '`') {
        var j = s.indexOf('`', i + 1);
        if (j !== -1) { out += '<span class="tok-str">' + esc(s.slice(i, j + 1)) + '</span>'; i = j + 1; continue; }
      }
      // Bold **...**
      if (s[i] === '*' && i + 1 < n && s[i + 1] === '*') {
        var j = s.indexOf('**', i + 2);
        if (j !== -1) { out += '<span class="tok-kw">' + esc(s.slice(i, j + 2)) + '</span>'; i = j + 2; continue; }
      }
      // Image ![alt](url)
      if (s[i] === '!' && i + 1 < n && s[i + 1] === '[') {
        var cb = s.indexOf(']', i + 2);
        if (cb !== -1 && cb + 1 < n && s[cb + 1] === '(') {
          var cp = s.indexOf(')', cb + 2);
          if (cp !== -1) {
            out += '<span class="tok-pp">!</span><span class="tok-prop">' + esc(s.slice(i + 1, cb + 1)) + '</span>' +
                   '<span class="tok-str">' + esc(s.slice(cb + 1, cp + 1)) + '</span>';
            i = cp + 1; continue;
          }
        }
      }
      // Link [text](url)
      if (s[i] === '[') {
        var cb = s.indexOf(']', i + 1);
        if (cb !== -1 && cb + 1 < n && s[cb + 1] === '(') {
          var cp = s.indexOf(')', cb + 2);
          if (cp !== -1) {
            out += '<span class="tok-prop">' + esc(s.slice(i, cb + 1)) + '</span>' +
                   '<span class="tok-str">' + esc(s.slice(cb + 1, cp + 1)) + '</span>';
            i = cp + 1; continue;
          }
        }
      }
      out += esc(s[i]); i++;
    }
    return out;
  }

  // Markdown line-oriented highlighter: headings, blockquotes, lists, fences, HR, inline markup.
  function highlightMarkdown(code) {
    var lines = code.split('\n');
    var out = '';
    var inFence = false;
    for (var li = 0; li < lines.length; li++) {
      var line = lines[li];
      if (li > 0) out += '\n';
      // Code fence ``` or ~~~
      if (/^(`{3,}|~{3,})/.test(line)) {
        out += '<span class="tok-pp">' + esc(line) + '</span>';
        inFence = !inFence; continue;
      }
      // Inside a fenced block — leave as-is
      if (inFence) { out += esc(line); continue; }
      // Horizontal rule --- *** ___
      if (/^(\*{3,}|-{3,}|_{3,})\s*$/.test(line)) { out += '<span class="tok-pp">' + esc(line) + '</span>'; continue; }
      // ATX heading # ## ...
      var hm = line.match(/^(#{1,6})(\s+.*)$/);
      if (hm) { out += '<span class="tok-kw">' + esc(hm[1]) + '</span>' + mdInline(hm[2]); continue; }
      // Setext heading underline === or ---
      if (/^(={3,}|-{3,})\s*$/.test(line)) { out += '<span class="tok-kw">' + esc(line) + '</span>'; continue; }
      // Blockquote > ...
      var bq = line.match(/^(>+\s?)(.*)$/);
      if (bq) { out += '<span class="tok-cmt">' + esc(bq[1]) + '</span>' + mdInline(bq[2]); continue; }
      // Unordered list item - * +
      var ul = line.match(/^(\s*)([-*+])(\s+.*)$/);
      if (ul) { out += esc(ul[1]) + '<span class="tok-kw">' + esc(ul[2]) + '</span>' + mdInline(ul[3]); continue; }
      // Ordered list item 1. 2. ...
      var ol = line.match(/^(\s*)(\d+\.)(\s+.*)$/);
      if (ol) { out += esc(ol[1]) + '<span class="tok-kw">' + esc(ol[2]) + '</span>' + mdInline(ol[3]); continue; }
      // Plain line with inline markup
      out += mdInline(line);
    }
    return out;
  }

  // XML/HTML state-machine highlighter — tag-aware, attribute-aware.
  function highlightXml(code) {
    var out = '';
    var i = 0;
    var n = code.length;
    try {
      while (i < n) {
        // HTML comment <!-- ... -->
        if (code.slice(i, i + 4) === '<!--') {
          var ec = code.indexOf('-->', i + 4);
          var sc = ec === -1 ? code.slice(i) : code.slice(i, ec + 3);
          out += '<span class="tok-cmt">' + esc(sc) + '</span>';
          i += sc.length; continue;
        }
        // CDATA section
        if (code.slice(i, i + 9) === '<![CDATA[') {
          var ecd = code.indexOf(']]>', i + 9);
          var scd = ecd === -1 ? code.slice(i) : code.slice(i, ecd + 3);
          out += '<span class="tok-str">' + esc(scd) + '</span>';
          i += scd.length; continue;
        }
        // Tag start
        if (code[i] === '<') {
          out += '<span class="tok-pp">&lt;</span>'; i++;
          // closing slash </tag>
          if (i < n && code[i] === '/') { out += '<span class="tok-pp">/</span>'; i++; }
          // tag name
          var tn = i;
          while (tn < n && /[a-zA-Z0-9:_.-]/.test(code[tn])) tn++;
          if (tn > i) { out += '<span class="tok-kw">' + esc(code.slice(i, tn)) + '</span>'; i = tn; }
          // attributes until > or />
          while (i < n && code[i] !== '>') {
            if (code[i] === '/' && i + 1 < n && code[i + 1] === '>') {
              out += '<span class="tok-pp">/&gt;</span>'; i += 2; break;
            }
            if (/[a-zA-Z_:.-]/.test(code[i])) {
              var an = i;
              while (an < n && /[a-zA-Z0-9_:.-]/.test(code[an])) an++;
              out += '<span class="tok-prop">' + esc(code.slice(i, an)) + '</span>'; i = an; continue;
            }
            if (code[i] === '"' || code[i] === "'") {
              var qa = code[i]; var ja = i + 1;
              while (ja < n && code[ja] !== qa) ja++;
              if (ja < n) ja++;
              out += '<span class="tok-str">' + esc(code.slice(i, ja)) + '</span>'; i = ja; continue;
            }
            out += esc(code[i]); i++;
          }
          if (i < n && code[i] === '>') { out += '<span class="tok-pp">&gt;</span>'; i++; }
          continue;
        }
        // XML entity reference &amp; &lt; &gt; &#160; …
        if (code[i] === '&') {
          var je = i + 1;
          while (je < n && code[je] !== ';' && code[je] !== '<' && je - i < 12) je++;
          if (je < n && code[je] === ';') {
            out += '<span class="tok-num">' + esc(code.slice(i, je + 1)) + '</span>'; i = je + 1; continue;
          }
        }
        out += esc(code[i]); i++;
      }
    } catch (_) { return esc(code); }
    return out;
  }

  // Tokenise `code` (raw, not HTML-escaped) and return highlighted HTML.
  function highlight(lang, code) {
    var family = langFamily(lang);
    if (family === 'xml') return highlightXml(code);
    if (family === 'md') return highlightMarkdown(code);
    var kw = family ? KW[family] : null;
    var isCpp = family === 'cpp';
    var isCss = family === 'css';
    var isJson = family === 'json';
    var isSql = family === 'sql';
    var isPy = family === 'py';
    var out = '';
    var i = 0;
    var n = code.length;
    try {
      while (i < n) {
        var c = code[i];
        // SQL line comment --
        if (isSql && c === '-' && i + 1 < n && code[i + 1] === '-') {
          var e = code.indexOf('\n', i);
          var seg = e === -1 ? code.slice(i) : code.slice(i, e);
          out += '<span class="tok-cmt">' + esc(seg) + '</span>';
          i += seg.length; continue;
        }
        // Python / Shell line comment #
        if (isPy && c === '#') {
          var e = code.indexOf('\n', i);
          var seg = e === -1 ? code.slice(i) : code.slice(i, e);
          out += '<span class="tok-cmt">' + esc(seg) + '</span>';
          i += seg.length; continue;
        }
        // Line comment //
        if (c === '/' && i + 1 < n && code[i + 1] === '/') {
          var e = code.indexOf('\n', i);
          var seg = e === -1 ? code.slice(i) : code.slice(i, e);
          out += '<span class="tok-cmt">' + esc(seg) + '</span>';
          i += seg.length; continue;
        }
        // Block comment /* */
        if (c === '/' && i + 1 < n && code[i + 1] === '*') {
          var e2 = code.indexOf('*/', i + 2);
          var seg2 = e2 === -1 ? code.slice(i) : code.slice(i, e2 + 2);
          out += '<span class="tok-cmt">' + esc(seg2) + '</span>';
          i += seg2.length; continue;
        }
        // Preprocessor directives #include, #define …
        if (isCpp && c === '#' && (i === 0 || code[i - 1] === '\n')) {
          var e3 = code.indexOf('\n', i);
          var seg3 = e3 === -1 ? code.slice(i) : code.slice(i, e3);
          out += '<span class="tok-pp">' + esc(seg3) + '</span>';
          i += seg3.length; continue;
        }
        // CSS @at-rules: @media, @import, @keyframes …
        if (isCss && c === '@') {
          var ka = i + 1;
          while (ka < n && /[a-zA-Z-]/.test(code[ka])) ka++;
          out += '<span class="tok-pp">' + esc(code.slice(i, ka)) + '</span>';
          i = ka; continue;
        }
        // CSS hex colours: #rgb, #rgba, #rrggbb, #rrggbbaa
        if (isCss && c === '#') {
          var kh = i + 1;
          while (kh < n && /[0-9a-fA-F]/.test(code[kh])) kh++;
          var hlen = kh - i - 1;
          if (hlen === 3 || hlen === 4 || hlen === 6 || hlen === 8) {
            out += '<span class="tok-num">' + esc(code.slice(i, kh)) + '</span>';
            i = kh; continue;
          }
          out += esc(c); i++; continue; // ID selector, not a colour
        }
        // Python triple-quoted strings """ or '''
        if (isPy && (c === '"' || c === "'") && i + 2 < n && code[i+1] === c && code[i+2] === c) {
          var q3 = c + c + c;
          var et = code.indexOf(q3, i + 3);
          var st = et === -1 ? code.slice(i) : code.slice(i, et + 3);
          out += '<span class="tok-str">' + esc(st) + '</span>';
          i += st.length; continue;
        }
        // String literals " ' ` — JSON keys get tok-prop, values get tok-str
        if (c === '"' || c === "'" || c === '`') {
          var q = c;
          var j = i + 1;
          while (j < n) {
            if (code[j] === '\\') { j += 2; continue; }
            if (code[j] === q) { j++; break; }
            if (code[j] === '\n' && q !== '`') break;
            j++;
          }
          var cls = 'tok-str';
          if (isJson && q === '"') {
            var pk = j;
            while (pk < n && (code[pk] === ' ' || code[pk] === '\t')) pk++;
            if (pk < n && code[pk] === ':') cls = 'tok-prop';
          }
          out += '<span class="' + cls + '">' + esc(code.slice(i, j)) + '</span>';
          i = j; continue;
        }
        // Numbers — for CSS also consume unit suffix (px, em, rem, %, vw, vh …)
        if (/[0-9]/.test(c) || (c === '.' && i + 1 < n && /[0-9]/.test(code[i + 1]))) {
          var kn = i;
          while (kn < n && /[0-9a-fA-FxXoObBlLuUfFdD._]/.test(code[kn])) kn++;
          if (isCss) { while (kn < n && /[a-zA-Z%]/.test(code[kn])) kn++; }
          out += '<span class="tok-num">' + esc(code.slice(i, kn)) + '</span>';
          i = kn; continue;
        }
        // CSS identifiers: property names (before ':') get tok-prop, value keywords get tok-kw
        if (isCss && /[a-zA-Z_]/.test(c)) {
          var ki = i;
          while (ki < n && /[a-zA-Z0-9_-]/.test(code[ki])) ki++;
          var word = code.slice(i, ki);
          var pk2 = ki;
          while (pk2 < n && (code[pk2] === ' ' || code[pk2] === '\t')) pk2++;
          var isProp = pk2 < n && code[pk2] === ':' && (pk2 + 1 >= n || code[pk2 + 1] !== ':');
          if (isProp) {
            out += '<span class="tok-prop">' + esc(word) + '</span>';
          } else if (kw && kw.has(word)) {
            out += '<span class="tok-kw">' + esc(word) + '</span>';
          } else {
            out += esc(word);
          }
          i = ki; continue;
        }
        // General identifiers / keywords — SQL is case-insensitive
        if (/[a-zA-Z_$]/.test(c)) {
          var k2 = i;
          while (k2 < n && /[a-zA-Z0-9_$]/.test(code[k2])) k2++;
          var word2 = code.slice(i, k2);
          var isKw = kw && (kw.has(word2) || (isSql && kw.has(word2.toUpperCase())));
          out += isKw ? '<span class="tok-kw">' + esc(word2) + '</span>' : esc(word2);
          i = k2; continue;
        }
        out += esc(c);
        i++;
      }
    } catch (_) {
      return esc(code); // safety fallback
    }
    return out;
  }

  // A fenced block renders as a header bar (language on the left, a copy button on the right)
  // sitting on top of the <pre>. The button is always visible rather than hover-only: the point
  // of it is that a suggested command can be taken without hand-selecting the text, and on a
  // narrow tool window a control that only appears on hover is one nobody finds. app.js owns the
  // click, delegated -- streaming rewrites this markup on every chunk, so a listener bound here
  // would end up on a node that is already gone.
  const COPY_ICON =
    '<svg class="cico" viewBox="0 0 16 16" width="12" height="12" aria-hidden="true">' +
    '<path d="M5.5 3.5h7a1 1 0 0 1 1 1v7" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round" />' +
    '<rect x="2.5" y="5.5" width="9" height="9" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2" /></svg>';

  function codeBlock(lang, code) {
    var body = langFamily(lang) ? highlight(lang, code) : esc(code);
    return '<div class="cblock"><div class="cbar"><span class="clang">' + esc(lang) + '</span>' +
      '<button type="button" class="ccopy" title="Copy code">' + COPY_ICON +
      '<span class="clabel">Copy</span></button></div>' +
      '<pre data-lang="' + esc(lang) + '"><code>' + body + '</code></pre></div>';
  }

  function render(md) {
    // Security: inline() round-trips code spans through a \u0000-delimited placeholder, and esc()
    // does not escape \u0000 — so text arriving with a literal NUL (JSON carries it fine) could
    // forge a placeholder. Harmless while codes[] only ever holds escaped content, but that is a
    // fragile invariant to rest the escaping on. Drop NULs at the door instead.
    md = (md || "").replace(/\r\n/g, "\n").replace(/\u0000/g, "");
    const lines = md.split("\n");
    let html = "";
    let i = 0;
    let listType = null;

    function closeList() {
      if (listType) { html += "</" + listType + ">"; listType = null; }
    }

    while (i < lines.length) {
      let line = lines[i];

      // fenced code block
      const fence = line.match(/^```(\w*)\s*$/);
      if (fence) {
        closeList();
        const lang = fence[1] || "";
        const buf = [];
        i++;
        while (i < lines.length && !/^```\s*$/.test(lines[i])) { buf.push(lines[i]); i++; }
        i++; // skip closing fence
        html += codeBlock(lang, buf.join("\n"));
        continue;
      }

      // heading
      const h = line.match(/^(#{1,6})\s+(.*)$/);
      if (h) { closeList(); html += "<h" + h[1].length + ">" + inline(h[2]) + "</h" + h[1].length + ">"; i++; continue; }

      // hr
      if (/^(\*\*\*|---|___)\s*$/.test(line)) { closeList(); html += "<hr />"; i++; continue; }

      // blockquote
      if (/^>\s?/.test(line)) { closeList(); html += '<blockquote>' + inline(line.replace(/^>\s?/, '')) + '</blockquote>'; i++; continue; }

      // unordered list
      const ul = line.match(/^[-*+]\s+(.*)$/);
      if (ul) {
        if (listType !== "ul") { closeList(); html += "<ul>"; listType = "ul"; }
        html += "<li>" + inline(ul[1]) + "</li>"; i++; continue;
      }
      // ordered list
      const ol = line.match(/^\d+\.\s+(.*)$/);
      if (ol) {
        if (listType !== "ol") { closeList(); html += "<ol>"; listType = "ol"; }
        html += "<li>" + inline(ol[1]) + "</li>"; i++; continue;
      }

      // table: header row followed by separator row (|---|---|)
      if (/\|/.test(line) && i + 1 < lines.length && /^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$/.test(lines[i + 1])) {
        closeList();
        var parseRow = function (r) { return r.replace(/^\||\|$/g, '').split('|').map(function (c) { return c.trim(); }); };
        var heads = parseRow(line);
        var aligns = parseRow(lines[i + 1]).map(function (c) {
          if (/^:-+:$/.test(c)) return "center";
          if (/^-+:$/.test(c)) return "right";
          return "left";
        });
        html += "<table><thead><tr>";
        heads.forEach(function (c, ci) { html += '<th style="text-align:' + (aligns[ci] || "left") + '">' + inline(c) + "</th>"; });
        html += "</tr></thead><tbody>";
        i += 2;
        while (i < lines.length && /\|/.test(lines[i])) {
          var cells = parseRow(lines[i]);
          html += "<tr>";
          cells.forEach(function (c, ci) { html += '<td style="text-align:' + (aligns[ci] || "left") + '">' + inline(c) + "</td>"; });
          html += "</tr>";
          i++;
        }
        html += "</tbody></table>";
        continue;
      }

      // blank
      if (/^\s*$/.test(line)) { closeList(); i++; continue; }

      // paragraph (merge consecutive non-blank, non-special lines)
      closeList();
      const para = [line];
      i++;
      while (i < lines.length && !/^\s*$/.test(lines[i]) && !/^(#{1,6}\s|```|>|[-*+]\s|\d+\.\s|\|)/.test(lines[i])) {
        para.push(lines[i]); i++;
      }
      html += "<p>" + para.map(inline).join("<br />") + "</p>";
    }
    closeList();
    return html;
  }

  global.md = { render: render, esc: esc };
})(window);