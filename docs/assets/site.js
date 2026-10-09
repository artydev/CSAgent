/* CsAgent site: terminal replay, copy buttons, per-OS commands. No dependencies. */
(function () {
  "use strict";
  var reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  /* ---------- terminal replay ---------- */
  var body = document.getElementById("termBody");
  if (body) {
    var gateBar = document.getElementById("gateBar");
    var replayBar = document.getElementById("replayBar");
    var allowBtn = document.getElementById("gateAllow");
    var denyBtn = document.getElementById("gateDeny");
    var replayBtn = document.getElementById("replayBtn");
    var token = 0;

    var BOX = 40;
    function top(label) { var s = "+- " + label + " "; return s + new Array(Math.max(BOX - s.length, 3) + 1).join("-"); }
    var bottom = "+" + new Array(BOX).join("-");
    function step(n) { var s = "-- Step " + n + "/30 "; return s + new Array(Math.max(BOX - s.length, 3) + 1).join("-"); }

    var head = [
      ["u", "typed", "List every TODO in this project into TODO.md, grouped by file"],
      ["", "", ""],
      ["d", "", step(1)],
      ["", "", ""],
      ["d", "", top("ASSISTANT")],
      ["", "", "|  I'll search the project for TODO markers first."],
      ["d", "", bottom],
      ["", "", ""],
      ["c", "", top("TOOL CALL: search_files")],
      ["", "", "|   pattern: TODO"],
      ["", "", "|   path: ."],
      ["", "", "|   glob: *.cs"],
      ["c", "", bottom],
      ["", "", ""],
      ["ok", "", top("RESULT")],
      ["", "", "|  src/Program.cs:3: // TODO: read the port from configuration"],
      ["", "", "|  src/OrderStore.cs:7: // TODO: add paging to List()"],
      ["", "", "|  src/OrderService.cs:7: // TODO: reject carts with more than 50 lines"],
      ["", "", "|  src/OrderService.cs:13: // TODO: cancel orders that are still unpaid after 24 hours"],
      ["", "", "|  tests/OrderServiceTests.cs:1: // TODO: cover Cancel() once the 24-hour rule exists"],
      ["ok", "", bottom],
      ["", "", ""],
      ["d", "", step(2)],
      ["", "", ""],
      ["d", "", top("ASSISTANT")],
      ["", "", "|  Five TODOs in four files. I'll group them by file in TODO.md."],
      ["d", "", bottom],
      ["", "", ""],
      ["c", "", top("TOOL CALL: write_file")],
      ["", "", "|   path: TODO.md"],
      ["", "", "|   content: # TODO\n|\n|   ## src/Program.cs\n|   - Read the port from configuration\n|   ... (290 chars total)"],
      ["c", "", bottom]
    ];
    var question = "? Allow destructive action 'write_file'? [Y/n] ";
    var tailAllow = [
      ["ok", "", top("RESULT")],
      ["", "", "|  OK: wrote 290 bytes to '~/orders-api/TODO.md'"],
      ["ok", "", bottom],
      ["", "", ""],
      ["d", "", step(3)],
      ["", "", ""],
      ["d", "", top("ASSISTANT")],
      ["", "", "|  Done. TODO.md lists the 5 TODOs, grouped by file. The two in OrderService.cs are the ones that change behaviour; the rest are small."],
      ["d", "", bottom],
      ["", "", ""],
      ["ok", "", "✓ Task complete."]
    ];
    var tailDeny = [
      ["er", "", top("RESULT")],
      ["", "", "|  Tool call declined by user."],
      ["er", "", bottom],
      ["", "", ""],
      ["d", "", step(3)],
      ["", "", ""],
      ["d", "", top("ASSISTANT")],
      ["", "", "|  Understood, nothing was written. The five TODOs are in the search result above; tell me if you want them in another format."],
      ["d", "", bottom],
      ["", "", ""],
      ["ok", "", "✓ Task complete."]
    ];

    var sleep = function (ms) { return new Promise(function (r) { setTimeout(r, reduce ? 0 : ms); }); };
    var stick = function () { body.scrollTop = body.scrollHeight; };

    function addSpan(cls, text) {
      var s = document.createElement("span");
      if (cls) s.className = cls;
      s.textContent = text;
      body.appendChild(s);
      return s;
    }

    async function play(list, my) {
      for (var i = 0; i < list.length; i++) {
        if (my !== token) return false;
        var l = list[i], cls = l[0], mode = l[1], text = l[2];
        if (mode === "typed") {
          addSpan("d", "> ");
          var sp = addSpan(cls, "");
          sp.classList.add("caret");
          await sleep(500);
          for (var k = 0; k < text.length; k++) {
            if (my !== token) return false;
            sp.textContent += text[k];
            if (!reduce) await sleep(22);
          }
          sp.classList.remove("caret");
          addSpan("", "\n");
          await sleep(350);
        } else {
          addSpan(cls, text + "\n");
          stick();
          var pause = text === "" ? 40 : 55;
          if (/^-- Step/.test(text)) pause = 420;
          await sleep(pause);
        }
      }
      return true;
    }

    function run() {
      var my = ++token;
      body.textContent = "";
      gateBar.classList.remove("on", "waiting");
      replayBar.classList.remove("on");
      allowBtn.disabled = false; denyBtn.disabled = false;
      (async function () {
        if (!(await play(head, my))) return;
        var q = addSpan("q", question);
        stick();
        gateBar.classList.add("on", "waiting");
        var answer = await new Promise(function (resolve) {
          function done(v) { cleanup(); resolve(v); }
          function onKey(e) {
            if (e.ctrlKey || e.metaKey || e.altKey) return;
            var t = e.target && e.target.tagName;
            if (t === "INPUT" || t === "TEXTAREA" || t === "SELECT") return;
            var k = (e.key || "").toLowerCase();
            if (k === "y") done(true);
            else if (k === "n") done(false);
          }
          function cleanup() {
            document.removeEventListener("keydown", onKey);
            allowBtn.onclick = null; denyBtn.onclick = null;
          }
          allowBtn.onclick = function () { done(true); };
          denyBtn.onclick = function () { done(false); };
          document.addEventListener("keydown", onKey);
          body._cancel = function () { cleanup(); resolve(null); };
        });
        if (my !== token || answer === null) return;
        gateBar.classList.remove("waiting");
        gateBar.classList.remove("on");
        q.textContent = question + (answer ? "y" : "n") + "\n";
        stick();
        await sleep(500);
        if (!(await play(answer ? tailAllow : tailDeny, my))) return;
        stick();
        replayBar.classList.add("on");
      })();
    }

    replayBtn.addEventListener("click", function () { if (body._cancel) body._cancel(); run(); });

    var started = false;
    function startOnce() { if (started) return; started = true; run(); }
    if ("IntersectionObserver" in window) {
      var io = new IntersectionObserver(function (entries) {
        entries.forEach(function (en) { if (en.isIntersecting) { startOnce(); io.disconnect(); } });
      }, { threshold: 0.45 });
      io.observe(document.getElementById("term"));
    } else { startOnce(); }
  }

  /* ---------- copy buttons ---------- */
  document.querySelectorAll(".pre-wrap").forEach(function (wrap) {
    var pre = wrap.querySelector("pre");
    if (!pre || !navigator.clipboard) return;
    var b = document.createElement("button");
    b.type = "button"; b.className = "copy"; b.textContent = "Copier";
    b.setAttribute("aria-label", "Copier ce code dans le presse-papiers");
    b.addEventListener("click", function () {
      navigator.clipboard.writeText(pre.innerText.replace(/\n+$/, "")).then(function () {
        b.textContent = "Copié";
        setTimeout(function () { b.textContent = "Copier"; }, 1600);
      }, function () { b.textContent = "Faites Ctrl+C"; });
    });
    wrap.appendChild(b);
  });

  /* ---------- per-OS commands ---------- */
  var OS = {
    win: { rid: "win-x64", cd: "cd bin\\Release\\net10.0\\win-x64\\publish", key: '$env:ALBERT_API_KEY = "your-key"', exe: ".\\CsAgent.exe" },
    linux: { rid: "linux-x64", cd: "cd bin/Release/net10.0/linux-x64/publish", key: 'export ALBERT_API_KEY="your-key"', exe: "./CsAgent" },
    mac: { rid: "osx-x64", cd: "cd bin/Release/net10.0/osx-x64/publish", key: 'export ALBERT_API_KEY="your-key"', exe: "./CsAgent" }
  };
  var tabs = document.querySelectorAll("[data-os]");
  if (tabs.length) {
    var guess = /Win/i.test(navigator.platform || navigator.userAgent) ? "win" : /Mac/i.test(navigator.platform || "") ? "mac" : "linux";
    var setOs = function (name) {
      var o = OS[name];
      tabs.forEach(function (t) { t.setAttribute("aria-pressed", String(t.getAttribute("data-os") === name)); });
      document.querySelectorAll("[data-fill]").forEach(function (n) { n.textContent = o[n.getAttribute("data-fill")]; });
    };
    tabs.forEach(function (t) { t.addEventListener("click", function () { setOs(t.getAttribute("data-os")); }); });
    setOs(guess);
  }
})();
