/* ---------------------------------------------------------------------------
   Chargle site
   Nothing here talks to a server. The sounds are the same files that ship with
   the app, the waveforms are drawn from peak data generated from those files,
   and the timing readout is measured in your own browser.
   --------------------------------------------------------------------------- */

(function () {
  "use strict";

  var reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var PEAKS = window.CHARGLE_PEAKS || {};

  var PACKS = [
    { id: "chime", name: "Chime", desc: "A soft mallet struck twice. Warm, short, and hard to get tired of. This is the default." },
    { id: "red-fruit", name: "Red Fruit", desc: "A softly struck tine with a wide room behind it. The most ceremonious sound here." },
    { id: "droplet", name: "Droplet", desc: "A single drop of water. Sweeps up when the cable lands and down when it leaves." },
    { id: "glass", name: "Glass", desc: "Struck crystal, with the long inharmonic shimmer a real bell leaves behind." },
    { id: "swell", name: "Swell", desc: "A warm synth pad that fades up rather than hitting. For people who dislike being pinged." },
    { id: "pebble", name: "Pebble", desc: "A dry wooden tap with a tiny pitch bend. Nearly silent, but you feel it land." },
    { id: "tick", name: "Tick", desc: "One frame of sound. The least this app can possibly say while still saying it." },
    { id: "blip", name: "Blip", desc: "Three square waves in a row, the way a machine would have told you in 1989." }
  ];

  var COLOURS = [
    { name: "Windows accent", hex: "#0078D4", note: "Whatever colour you have chosen in Windows. Shown here with the default blue." },
    { name: "Blue", hex: "#2563EB", note: "Used while the charger is connected. On battery it stays grey." },
    { name: "Cyan", hex: "#06B6D4", note: "Used while the charger is connected. On battery it stays grey." },
    { name: "Green", hex: "#16A34A", note: "Used while the charger is connected. On battery it stays grey." },
    { name: "Amber", hex: "#D97706", note: "Used while the charger is connected. On battery it stays grey." },
    { name: "Rose", hex: "#E11D48", note: "Used while the charger is connected. On battery it stays grey." },
    { name: "Violet", hex: "#7C3AED", note: "Used while the charger is connected. On battery it stays grey." },
    { name: "Slate", hex: "#475569", note: "Used while the charger is connected. On battery it stays grey." }
  ];

  var $ = function (sel, root) { return (root || document).querySelector(sel); };

  /* --- theme -------------------------------------------------------------- */

  var toggle = $("#themeToggle");
  toggle.addEventListener("click", function () {
    var systemDark = window.matchMedia("(prefers-color-scheme: dark)").matches;
    var current = document.documentElement.dataset.theme || (systemDark ? "dark" : "light");
    var next = current === "dark" ? "light" : "dark";
    document.documentElement.dataset.theme = next;
    localStorage.setItem("chargle-theme", next);
    document.querySelector('meta[name="theme-color"]').setAttribute("content", next === "dark" ? "#0a0806" : "#ece7dd");
    drawAllWaveforms();
  });

  /* --- masthead and the cable down the side ------------------------------- */

  var railFill = $("#railFill");
  var masthead = $("#masthead");
  var ticking = false;

  function onScroll() {
    if (ticking) return;
    ticking = true;
    requestAnimationFrame(function () {
      var doc = document.documentElement;
      var max = doc.scrollHeight - window.innerHeight;
      var progress = max > 0 ? Math.min(1, window.scrollY / max) : 0;
      railFill.style.height = (progress * 100).toFixed(2) + "%";
      masthead.dataset.stuck = window.scrollY > 8 ? "yes" : "no";
      ticking = false;
    });
  }
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  /* --- reveal ------------------------------------------------------------- */

  var revealer = new IntersectionObserver(
    function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) {
          e.target.classList.add("seen");
          revealer.unobserve(e.target);
        }
      });
    },
    { rootMargin: "0px 0px -12% 0px" }
  );
  Array.prototype.forEach.call(document.querySelectorAll(".up"), function (el, i) {
    el.style.setProperty("--i", i % 4);
    revealer.observe(el);
  });

  /* --- audio -------------------------------------------------------------- */

  var ctx = null;
  var buffers = {};
  var pending = {};

  function audio() {
    if (!ctx) {
      var Ctor = window.AudioContext || window.webkitAudioContext;
      if (!Ctor) return null;
      ctx = new Ctor();
    }
    if (ctx.state === "suspended") ctx.resume();
    return ctx;
  }

  function key(pack, kind) { return pack + "/" + kind; }

  function load(pack, kind) {
    var k = key(pack, kind);
    if (buffers[k]) return Promise.resolve(buffers[k]);
    if (pending[k]) return pending[k];
    var c = audio();
    if (!c) return Promise.reject(new Error("no audio"));
    pending[k] = fetch("sounds/" + pack + "/" + kind + ".wav")
      .then(function (r) { return r.arrayBuffer(); })
      .then(function (bytes) {
        return new Promise(function (resolve, reject) { c.decodeAudioData(bytes, resolve, reject); });
      })
      .then(function (buf) {
        buffers[k] = buf;
        delete pending[k];
        return buf;
      });
    return pending[k];
  }

  var volume = 0.65;

  // Returns the number of milliseconds between the input event and the sound
  // leaving the browser, output latency included.
  function play(pack, kind, startedAt) {
    var c = audio();
    if (!c) return Promise.resolve(null);
    return load(pack, kind).then(function (buf) {
      var src = c.createBufferSource();
      var gain = c.createGain();
      gain.gain.value = volume;
      src.buffer = buf;
      src.connect(gain).connect(c.destination);
      src.start();
      if (startedAt == null) return null;
      var latency = (c.outputLatency || c.baseLatency || 0) * 1000;
      return performance.now() - startedAt + latency;
    }).catch(function () { return null; });
  }

  /* --- waveforms ---------------------------------------------------------- */

  var canvases = [];

  function cssVar(name) {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  }

  // Every waveform is drawn on the same time axis, so a 26 ms tick is a sliver
  // next to a 923 ms swell. The shapes are worth comparing and the lengths are
  // worth comparing, and this way one picture does both.
  var LONGEST = 0;
  Object.keys(PEAKS).forEach(function (id) {
    LONGEST = Math.max(LONGEST, PEAKS[id].plug.seconds, PEAKS[id].unplug.seconds);
  });

  function drawWave(canvas, data, progress) {
    var dpr = window.devicePixelRatio || 1;
    var w = canvas.clientWidth;
    var h = canvas.clientHeight;
    if (!w || !h) return;
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
    var g = canvas.getContext("2d");
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);

    var peaks = data.peaks;
    var n = peaks.length;
    var span = Math.max(6, w * (data.seconds / LONGEST));
    var barW = Math.max(1, span / n - 1);
    var mid = h / 2;
    var idle = cssVar("--text-3");
    var hot = cssVar("--accent");

    // the rest of the second, which is silence
    g.globalAlpha = 0.35;
    g.fillStyle = idle;
    g.fillRect(0, mid - 0.5, w, 1);

    for (var i = 0; i < n; i++) {
      var x = (i / n) * span;
      // A gentle curve on the amplitude, otherwise every pack is one spike and
      // a flat line and you cannot see the tail that makes them different.
      var amp = Math.max(0.03, Math.pow(peaks[i], 0.7)) * (h / 2 - 1);
      var played = progress != null && i / n <= progress;
      g.fillStyle = played ? hot : idle;
      g.globalAlpha = played ? 1 : 0.8;
      g.fillRect(x, mid - amp, barW, amp * 2);
    }
    g.globalAlpha = 1;
  }

  function drawAllWaveforms() {
    canvases.forEach(function (c) { drawWave(c.el, c.data, c.progress); });
  }

  window.addEventListener("resize", drawAllWaveforms);

  function sweep(entry, seconds) {
    if (reduced) {
      entry.progress = null;
      drawWave(entry.el, entry.data, null);
      return;
    }
    var start = performance.now();
    if (entry.raf) cancelAnimationFrame(entry.raf);
    function step(now) {
      var t = (now - start) / (seconds * 1000);
      if (t >= 1) {
        entry.progress = null;
        drawWave(entry.el, entry.data, null);
        entry.raf = null;
        return;
      }
      entry.progress = t;
      drawWave(entry.el, entry.data, t);
      entry.raf = requestAnimationFrame(step);
    }
    entry.raf = requestAnimationFrame(step);
  }

  /* --- the rack ----------------------------------------------------------- */

  var currentPack = "chime";
  var rack = $("#rack");
  var roPack = $("#roPack");
  var waveEntries = {};

  function playIcon() {
    return '<svg viewBox="0 0 12 12" fill="currentColor" aria-hidden="true"><path d="M2 1.2 10.4 6 2 10.8z"/></svg>';
  }

  PACKS.forEach(function (pack) {
    var data = PEAKS[pack.id];
    if (!data) return;

    var row = document.createElement("div");
    row.className = "pack";
    row.dataset.pack = pack.id;
    row.dataset.current = pack.id === currentPack ? "yes" : "no";
    row.innerHTML =
      '<div class="pack-name"><h3>' + pack.name + "</h3>" +
      '<span class="len">' + Math.round(data.plug.seconds * 1000) + " ms</span></div>" +
      '<p class="pack-desc">' + pack.desc + "</p>" +
      "<canvas></canvas>" +
      '<div class="pack-play">' +
      '<button class="pill" type="button" data-kind="plug">' + playIcon() + " in</button>" +
      '<button class="pill" type="button" data-kind="unplug">' + playIcon() + " out</button>" +
      "</div>";

    rack.appendChild(row);

    var canvas = row.querySelector("canvas");
    var entry = { el: canvas, data: data.plug, progress: null, raf: null };
    waveEntries[pack.id] = entry;
    canvases.push(entry);

    row.addEventListener("pointerenter", function () { load(pack.id, "plug"); }, { once: true });

    row.querySelectorAll(".pill").forEach(function (btn) {
      btn.addEventListener("click", function () {
        var kind = btn.dataset.kind;
        entry.data = data[kind];
        play(pack.id, kind, null);
        sweep(entry, data[kind].seconds);
        choosePack(pack.id);
      });
    });
  });

  function choosePack(id) {
    currentPack = id;
    roPack.textContent = (PACKS.filter(function (p) { return p.id === id; })[0] || {}).name || id;
    rack.querySelectorAll(".pack").forEach(function (r) {
      r.dataset.current = r.dataset.pack === id ? "yes" : "no";
    });
  }

  requestAnimationFrame(drawAllWaveforms);
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(drawAllWaveforms);

  /* --- the on screen panel, shared by the hero and the playground --------- */

  function foregroundOn(hex) {
    // The same rule the app uses, relative luminance rather than a brightness guess.
    var r = parseInt(hex.slice(1, 3), 16) / 255;
    var g = parseInt(hex.slice(3, 5), 16) / 255;
    var b = parseInt(hex.slice(5, 7), 16) / 255;
    function chan(v) { return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); }
    var lum = 0.2126 * chan(r) + 0.7152 * chan(g) + 0.0722 * chan(b);
    return lum > 0.4 ? "#000000" : "#ffffff";
  }

  function describe(osd, source, percent) {
    var head = source === "ac" ? "Plugged in" : "On battery";
    var detail = source === "ac" ? (percent >= 100 ? "Battery full" : "Charging, " + percent + "%") : percent + "% remaining";
    osd.querySelector(".osd-head").textContent = head;
    osd.querySelector(".osd-detail").textContent = detail;
    osd.dataset.power = source;
  }

  /* --- hero rig ----------------------------------------------------------- */

  var stage = $("#stage");
  var laptop = $("#laptop");
  var plug = $("#plug");
  var hint = $(".plug-hint");
  var cableLine = $("#cableLine");
  var cableCore = $("#cableCore");
  var heroOsd = $("#heroOsd");
  var trayPercent = $("#trayPercent");
  var roEvent = $("#roEvent");
  var roMs = $("#roMs");

  var seat = 0;          // 0 is fully out, 1 is seated
  var seatTarget = 0;
  var travel = 120;
  var geom = { left: 0, top: 0 };
  var percent = 78;
  var hideTimer = null;
  var tickTimer = null;

  function measure() {
    var stageBox = stage.getBoundingClientRect();
    var socket = laptop.querySelector(".socket").getBoundingClientRect();
    var plugBox = plug.getBoundingClientRect();
    travel = Math.max(74, Math.min(124, stageBox.width * 0.19));
    geom.left = socket.left - stageBox.left + 2;
    // Never let the plug park outside the stage, or narrow screens end up with
    // a page that is wider than the window.
    travel = Math.max(30, Math.min(travel, stageBox.width - geom.left - plugBox.width - 2));
    geom.top = socket.top - stageBox.top + socket.height / 2 - plugBox.height / 2;
    geom.w = stageBox.width;
    geom.h = stageBox.height;
    geom.plugW = plugBox.width;
    geom.plugH = plugBox.height;
    render();
  }

  function render() {
    var x = geom.left + (1 - seat) * travel;
    plug.style.left = x + "px";
    plug.style.top = geom.top + "px";
    hint.style.top = geom.top - 24 + "px";
    hint.style.left = Math.max(0, Math.min(x, geom.w - hint.offsetWidth)) + "px";

    // The cable leaves the back of the plug, sags under its own weight and runs
    // off the bottom right corner.
    var ax = x + geom.plugW;
    var ay = geom.top + geom.plugH / 2;
    var ex = geom.w * 0.86;
    var ey = geom.h + 6;
    var slack = 20 + seat * 55;
    var d =
      "M" + ax + " " + ay +
      " C " + (ax + 60 + slack) + " " + (ay + 6) +
      ", " + (ex + 90) + " " + (ey - 40) +
      ", " + ex + " " + ey;
    cableLine.setAttribute("d", d);
    cableCore.setAttribute("d", d);
  }

  function setPower(source) {
    laptop.dataset.power = source;
    describe(heroOsd, source, percent);
    heroOsd.dataset.shown = "yes";
    clearTimeout(hideTimer);
    hideTimer = setTimeout(function () { heroOsd.dataset.shown = "no"; }, 3200);

    clearInterval(tickTimer);
    tickTimer = setInterval(function () {
      if (source === "ac" && percent < 100) percent++;
      else if (source === "dc" && percent > 2) percent--;
      else return;
      trayPercent.textContent = percent + "%";
      if (heroOsd.dataset.shown === "yes") describe(heroOsd, source, percent);
    }, source === "ac" ? 2000 : 3000);
  }

  function fire(seated, at) {
    stage.dataset.touched = "yes";
    if (seated) {
      stage.dataset.seated = "yes";
      setTimeout(function () { stage.dataset.seated = "no"; }, 750);
    }
    var kind = seated ? "plug" : "unplug";
    roEvent.textContent = seated ? "charger connected" : "charger removed";
    var entry = waveEntries[currentPack];
    var data = PEAKS[currentPack][kind];
    if (entry) {
      entry.data = data;
      sweep(entry, data.seconds);
    }
    play(currentPack, kind, at).then(function (ms) {
      roMs.textContent = ms == null ? "not measured" : ms.toFixed(1) + " ms";
    });
    setPower(seated ? "ac" : "dc");
  }

  var animating = null;

  function settle(target, at) {
    var from = seat;
    var changed = (target === 1) !== (seatTarget === 1);
    seatTarget = target;
    plug.setAttribute("aria-pressed", target === 1 ? "true" : "false");
    if (changed) fire(target === 1, at);

    if (reduced) {
      seat = target;
      render();
      return;
    }
    if (animating) cancelAnimationFrame(animating);
    var start = performance.now();
    var dur = 320;
    function step(now) {
      var t = Math.min(1, (now - start) / dur);
      // ease out with a small overshoot, so it feels like it clicks home
      var e = 1 - Math.pow(1 - t, 3);
      var over = target === 1 ? Math.sin(t * Math.PI) * 0.04 : 0;
      seat = from + (target - from) * e + over;
      render();
      if (t < 1) animating = requestAnimationFrame(step);
      else { seat = target; render(); animating = null; }
    }
    animating = requestAnimationFrame(step);
  }

  var drag = null;

  plug.addEventListener("pointerdown", function (e) {
    plug.setPointerCapture(e.pointerId);
    drag = { x: e.clientX, seat: seat, moved: 0 };
    if (animating) { cancelAnimationFrame(animating); animating = null; }
    audio();
  });

  plug.addEventListener("pointermove", function (e) {
    if (!drag) return;
    var dx = e.clientX - drag.x;
    drag.moved = Math.max(drag.moved, Math.abs(dx));
    seat = Math.max(0, Math.min(1, drag.seat - dx / travel));
    render();
  });

  function endDrag(e) {
    if (!drag) return;
    var was = drag;
    drag = null;
    var at = performance.now();
    if (was.moved < 4) {
      settle(seatTarget === 1 ? 0 : 1, at);
      return;
    }
    settle(seat > 0.55 ? 1 : 0, at);
  }

  plug.addEventListener("pointerup", endDrag);
  plug.addEventListener("pointercancel", endDrag);

  plug.addEventListener("keydown", function (e) {
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      audio();
      settle(seatTarget === 1 ? 0 : 1, performance.now());
    }
  });

  plug.addEventListener("click", function (e) { e.preventDefault(); });

  window.addEventListener("resize", measure);
  measure();
  if (document.fonts && document.fonts.ready) document.fonts.ready.then(measure);
  window.addEventListener("load", measure);

  /* --- the playground ----------------------------------------------------- */

  var deskOsd = $("#deskOsd");
  var styleNote = $("#styleNote");
  var colourNote = $("#colourNote");
  var replay = null;

  var STYLE_NOTES = {
    panel: "The state and the battery level.",
    compact: "The state on its own, without the level.",
    minimal: "Just the mark, for people who only need to know that something happened."
  };

  function flash() {
    if (reduced) { deskOsd.dataset.shown = "yes"; return; }
    deskOsd.dataset.shown = "no";
    clearTimeout(replay);
    replay = setTimeout(function () { deskOsd.dataset.shown = "yes"; }, 180);
  }

  function knob(rootId, onPick) {
    var root = $(rootId);
    root.addEventListener("click", function (e) {
      var btn = e.target.closest(".pill");
      if (!btn) return;
      root.querySelectorAll(".pill").forEach(function (b) {
        b.setAttribute("aria-pressed", b === btn ? "true" : "false");
      });
      onPick(btn.dataset.value);
      flash();
    });
  }

  knob("#styleKnob", function (value) {
    deskOsd.dataset.style = value;
    styleNote.textContent = STYLE_NOTES[value];
  });

  knob("#placeKnob", function (value) {
    deskOsd.dataset.place = value;
  });

  var colourKnob = $("#colourKnob");
  COLOURS.forEach(function (colour, i) {
    var b = document.createElement("button");
    b.className = "swatch";
    b.type = "button";
    b.style.setProperty("--c", colour.hex);
    b.setAttribute("aria-pressed", i === 0 ? "true" : "false");
    b.setAttribute("aria-label", colour.name);
    b.title = colour.name;
    b.addEventListener("click", function () {
      colourKnob.querySelectorAll(".swatch").forEach(function (s) {
        s.setAttribute("aria-pressed", s === b ? "true" : "false");
      });
      deskOsd.style.setProperty("--osd-accent", colour.hex);
      deskOsd.style.setProperty("--osd-fg", foregroundOn(colour.hex));
      colourNote.textContent = colour.name + ". " + colour.note;
      flash();
    });
    colourKnob.appendChild(b);
  });

  deskOsd.style.setProperty("--osd-accent", COLOURS[0].hex);
  deskOsd.style.setProperty("--osd-fg", foregroundOn(COLOURS[0].hex));

  /* --- the four pages ----------------------------------------------------- */

  var SHOTS = {
    sound: {
      file: "screenshot-sound.png",
      alt: "The Chargle window on the Sound page, listing the eight packs with a waveform beside each one",
      cap: "Every pack in the list, with the waveform the app draws for each one"
    },
    screen: {
      file: "screenshot-screen.png",
      alt: "The On screen page, with settings for how much the panel says, its colour, where it appears and how long it stays",
      cap: "How much the panel says, what colour it is, where it sits and how long it stays"
    },
    rules: {
      file: "screenshot-rules.png",
      alt: "The Rules page, with switches for the charger being connected, disconnected and staying quiet when you are busy",
      cap: "When it speaks up, and when it keeps out of the way"
    },
    about: {
      file: "screenshot-about.png",
      alt: "The About page, showing the version, the theme setting, the licence and what the app was built with",
      cap: "Version, theme, licence, and what the whole thing is built out of"
    }
  };

  var shots = $(".shots");
  var shotTabs = $("#shotTabs");
  var shotImg = $("#shotImg");
  var shotCap = $("#shotCap");
  var shotsWarmed = false;

  function warmShots() {
    if (shotsWarmed) return;
    shotsWarmed = true;
    Object.keys(SHOTS).forEach(function (k) {
      var img = new Image();
      img.src = SHOTS[k].file;
    });
  }

  shotTabs.addEventListener("pointerenter", warmShots, { once: true });

  shotTabs.addEventListener("click", function (e) {
    var btn = e.target.closest(".pill");
    if (!btn) return;
    var shot = SHOTS[btn.dataset.shot];
    if (!shot || shotImg.getAttribute("src") === shot.file) return;
    warmShots();
    shotTabs.querySelectorAll(".pill").forEach(function (b) {
      b.setAttribute("aria-pressed", b === btn ? "true" : "false");
    });
    shots.dataset.swapping = "yes";
    setTimeout(function () {
      shotImg.src = shot.file;
      shotImg.alt = shot.alt;
      shotCap.textContent = shot.cap;
      shots.dataset.swapping = "no";
    }, reduced ? 0 : 200);
  });

  /* --- the signal path and the measured bars ------------------------------ */

  var lanes = $("#lanes");
  var laneLoop = null;

  function runLanes() {
    var els = lanes.querySelectorAll(".lane");
    els.forEach(function (l) { l.dataset.run = "no"; });
    requestAnimationFrame(function () {
      els.forEach(function (l) { l.dataset.run = "yes"; });
    });
  }

  var laneWatcher = new IntersectionObserver(function (entries) {
    entries.forEach(function (e) {
      if (e.isIntersecting && !reduced) {
        runLanes();
        clearInterval(laneLoop);
        laneLoop = setInterval(runLanes, 3600);
      } else {
        clearInterval(laneLoop);
      }
    });
  }, { threshold: 0.4 });
  laneWatcher.observe(lanes);

  var benchWatcher = new IntersectionObserver(function (entries) {
    entries.forEach(function (e) {
      if (!e.isIntersecting) return;
      e.target.querySelectorAll(".bar i").forEach(function (bar, i) {
        setTimeout(function () { bar.style.width = bar.dataset.w + "%"; }, reduced ? 0 : i * 160);
      });
      benchWatcher.unobserve(e.target);
    });
  }, { threshold: 0.3 });
  benchWatcher.observe($("#bench"));

  /* --- first impression --------------------------------------------------- */

  // Show the panel once on arrival, the way the app does when it starts, then
  // let it fade. The laptop screen should never just sit there empty.
  describe(heroOsd, "dc", percent);
  setTimeout(function () {
    if (stage.dataset.touched === "yes") return;
    heroOsd.dataset.shown = "yes";
    setTimeout(function () {
      if (stage.dataset.touched !== "yes") heroOsd.dataset.shown = "no";
    }, 3400);
  }, 900);
})();
