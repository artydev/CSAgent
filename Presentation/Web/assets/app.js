// =============================================================================
// CSAgent Console — Frontend Application
// =============================================================================
// Responsibilities:
//   1. Parse Markdown and apply Prism syntax highlighting
//   2. Handle user input and SSE (Server-Sent Events) chat stream
//   3. Render messages into the log container
//   4. Display step counter in the header
// =============================================================================

// -----------------------------------------------------------------------------
// SECTION 1 — Markdown & Syntax Highlighting
// -----------------------------------------------------------------------------

/**
 * Normalise language class names so Prism can highlight them correctly.
 *
 * Prism uses 'markup' internally for HTML/XML/SVG, but Marked.js generates
 * 'language-html' / 'language-xml'. We also map plain/text aliases to 'none'
 * so Prism does not attempt highlighting.
 *
 * @param {string} className — The original class attribute value
 * @returns {string} — The corrected class attribute value
 */
function normaliseLanguageClass(className) {
    let result = className;

    // Map 'html' and 'xml' to Prism's internal 'markup'
    result = result.replace("language-html", "language-markup");
    result = result.replace("language-xml", "language-markup");

    // Map plain-text aliases to 'none' (no highlighting)
    result = result.replace(
        /language-(text|plain|plaintext)/g,
        "language-none"
    );

    return result;
}

/**
 * Ensure Prism language aliases are set up globally.
 *
 * Prism's 'markup' grammar covers HTML, XML and SVG, but it does not
 * register 'html' or 'xml' as top-level language keys by default.
 */
function ensurePrismAliases() {
    if (typeof Prism === "undefined") return;

    if (Prism.languages.markup && !Prism.languages.html) {
        Prism.languages.html = Prism.languages.markup;
    }
    if (Prism.languages.markup && !Prism.languages.xml) {
        Prism.languages.xml = Prism.languages.markup;
    }
}

/**
 * Fix language classes on every code/pre element inside a container so that
 * Prism can recognise them.
 *
 * @param {HTMLElement} container — The parent element to search within
 */
function fixCodeLanguageClasses(container) {
    const selector = 'code[class*="language-"], pre[class*="language-"]';
    container.querySelectorAll(selector).forEach((element) => {
        element.className = normaliseLanguageClass(element.className);
    });
}

/**
 * Parse a Markdown string into an HTML element and apply syntax highlighting.
 *
 * @param {string} text — Raw Markdown content
 * @returns {HTMLDivElement} — A div.markdown-content containing the rendered HTML
 */
function parseMarkdown(text) {
    const container = document.createElement("div");
    container.className = "markdown-content";
    container.innerHTML = marked.parse(text);

    ensurePrismAliases();
    fixCodeLanguageClasses(container);
    Prism.highlightAllUnder(container);

    return container;
}

// -----------------------------------------------------------------------------
// SECTION 2 — Message Rendering
// -----------------------------------------------------------------------------

/**
 * Create a DOM element for a "done" message (task completed).
 *
 * @returns {HTMLDivElement}
 */
function createDoneMessage() {
    const div = document.createElement("div");
    div.className = "done";
    div.innerText = "✓ Task completed successfully";
    return div;
}

/**
 * Create a DOM element for a "warning" message.
 *
 * @param {string} text — The warning text
 * @returns {HTMLDivElement}
 */
function createWarningMessage(text) {
    const div = document.createElement("div");
    div.className = "warning";
    div.innerText = "⚠ " + text;
    return div;
}

/**
 * Create a DOM element for a "danger" (error) message.
 *
 * @param {string} text — The error text
 * @returns {HTMLDivElement}
 */
function createDangerMessage(text) {
    const div = document.createElement("div");
    div.className = "danger";
    div.innerText = "✗ " + text;
    return div;
}

/**
 * Create a DOM element for a tool call message.
 *
 * Displays the tool name prominently and formats the arguments
 * as a structured list of key-value pairs.
 *
 * @param {string} name — The tool name (e.g. "write_file", "read_file")
 * @param {string} argsJson — JSON string of the tool arguments
 * @returns {HTMLDivElement}
 */
function createToolCallMessage(name, argsJson) {
    const div = document.createElement("div");
    div.className = "call";

    // Tool name header
    const header = document.createElement("div");
    header.className = "call-header";

    // Map tool names to readable labels with icons
    const toolLabels = {
        "write_file": "📝 Write File",
        "read_file": "📖 Read File",
        "list_dir": "📂 List Directory",
        "search_files": "🔍 Search Files",
        "sh": "💻 Shell Command",
        "switch_model": "🔄 Switch Model",
        "list_models": "🤖 List Models",
        "read_clipboard": "📋 Read Clipboard",
        "write_clipboard": "📋 Write Clipboard"
    };
    header.innerHTML = `<strong>${toolLabels[name] || "🔧 " + name}</strong>`;
    div.appendChild(header);

    // Parse and display arguments
    try {
        const args = JSON.parse(argsJson);
        const argList = document.createElement("div");
        argList.className = "call-args";

        for (const [key, value] of Object.entries(args)) {
            const argRow = document.createElement("div");
            argRow.className = "call-arg-row";

            const keySpan = document.createElement("span");
            keySpan.className = "call-arg-key";
            keySpan.textContent = key + ":";
            argRow.appendChild(keySpan);

            const valSpan = document.createElement("span");
            valSpan.className = "call-arg-value";

            // Truncate very long values
            let displayVal = String(value);
            if (displayVal.length > 300) {
                displayVal = displayVal.substring(0, 300) + `... (${displayVal.length} chars total)`;
            }
            valSpan.textContent = displayVal;
            argRow.appendChild(valSpan);

            argList.appendChild(argRow);
        }

        div.appendChild(argList);
    } catch {
        // Fallback: show raw JSON in a styled pre block
        const raw = document.createElement("pre");
        raw.className = "call-raw";
        raw.textContent = argsJson;
        div.appendChild(raw);
    }

    return div;
}

/**
 * Create a DOM element for a tool result message.
 *
 * Tool results are raw data (file contents, command output, errors),
 * NOT Markdown. They are displayed as plain text in a code block
 * to avoid Markdown rendering issues (e.g. '#' in file contents
 * being treated as headings).
 *
 * @param {string} content — The raw result text
 * @param {boolean} isError — Whether this is an error result
 * @returns {HTMLDivElement}
 */
function createToolResultMessage(content, isError) {
    const div = document.createElement("div");
    div.className = isError ? "danger" : "result";

    // Show a brief header
    const header = document.createElement("div");
    header.className = "result-header";
    header.textContent = isError ? "✗ Error" : "✓ Result";
    div.appendChild(header);

    // Wrap content in a pre block for plain-text display
    const pre = document.createElement("pre");
    pre.className = "result-content";
    pre.textContent = content;
    div.appendChild(pre);

    return div;
}

/**
 * Create a DOM element for a generic log message.
 *
 * @param {string} type — The message type (used as CSS class)
 * @param {string} content — The text content
 * @returns {HTMLDivElement}
 */
function createGenericMessage(type, content) {
    const div = document.createElement("div");
    div.className = type;

    if (type === "thought") {
        // Assistant thoughts are Markdown-formatted text
        div.appendChild(parseMarkdown(content));
    } else {
        div.innerText = `[${type}] ${content}`;
    }

    return div;
}

/**
 * Create a confirmation block with Approve / Decline buttons.
 *
 * @param {string} toolName — The destructive tool awaiting approval
 * @returns {HTMLDivElement}
 */
function createConfirmBlock(toolName) {
    const wrap = document.createElement("div");
    wrap.className = "confirm-block";
    wrap.dataset.pending = "1";

    const label = document.createElement("div");
    label.className = "confirm-label";
    label.textContent = `⚠ Allow destructive action: ${toolName}`;
    wrap.appendChild(label);

    const btnWrap = document.createElement("div");
    btnWrap.className = "confirm-btns";

    function resolve(allow) {
        if (!wrap.dataset.pending) return;
        delete wrap.dataset.pending;
        btnWrap.remove();
        const resp = document.createElement("span");
        resp.className = allow ? "confirm-approved" : "confirm-declined";
        resp.textContent = allow ? "✓ Approved" : "✗ Declined";
        wrap.appendChild(resp);
        fetch("/api/confirm", {
            method: "POST",
            headers: { "Content-Type": "text/plain" },
            body: allow ? "true" : "false"
        }).catch(err => console.error("confirm POST failed:", err));
    }

    const approveBtn = document.createElement("button");
    approveBtn.className = "confirm-btn confirm-approve";
    approveBtn.textContent = "✓ Approve";
    approveBtn.addEventListener("click", () => resolve(true));

    const declineBtn = document.createElement("button");
    declineBtn.className = "confirm-btn confirm-decline";
    declineBtn.textContent = "✗ Decline";
    declineBtn.addEventListener("click", () => resolve(false));

    btnWrap.appendChild(approveBtn);
    btnWrap.appendChild(declineBtn);
    wrap.appendChild(btnWrap);
    return wrap;
}

/**
 * Route an incoming SSE message to the correct renderer and append it to the log.
 *
 * @param {object} message — Parsed JSON object with `type` and `data` fields
 * @param {HTMLElement} log — The log container element
 */
function appendMessageToLog(message, log) {
    let element;

    switch (message.type) {
        case "done":
            element = createDoneMessage();
            break;
        case "warning":
            element = createWarningMessage(
                typeof message.data === "string" ? message.data : JSON.stringify(message.data)
            );
            break;
        case "danger":
            element = createDangerMessage(
                typeof message.data === "string" ? message.data : JSON.stringify(message.data)
            );
            break;
        case "call":
            // Tool call messages have data: { n: toolName, a: argsJson }
            if (message.data && typeof message.data === "object" && message.data.n) {
                element = createToolCallMessage(message.data.n, message.data.a);
            } else {
                element = createGenericMessage(message.type, JSON.stringify(message.data));
            }
            break;
        case "result":
            // Tool result messages have data: { r: resultText, e: isError }
            if (message.data && typeof message.data === "object" && "r" in message.data) {
                element = createToolResultMessage(message.data.r, message.data.e);
            } else {
                element = createGenericMessage(message.type, JSON.stringify(message.data));
            }
            break;
        case "confirm": {
            const toolName = typeof message.data === "object" ? message.data.tool : message.data;
            element = createConfirmBlock(toolName);
            break;
        }
        default:
            element = createGenericMessage(
                message.type,
                typeof message.data === "string" ? message.data : JSON.stringify(message.data)
            );
            break;
    }

    log.appendChild(element);
}

/**
 * Scroll the log container to the bottom.
 *
 * @param {HTMLElement} log
 */
function scrollToBottom(log) {
    log.scrollTop = log.scrollHeight;
}

// -----------------------------------------------------------------------------
// SECTION 3 — Step Counter
// -----------------------------------------------------------------------------

/**
 * Update the step counter in the header.
 *
 * The step event data has the shape { n: currentStep, m: maxSteps }.
 * When the task is done or an error occurs, reset to "Ready".
 *
 * @param {object} data — The step data object
 */
function updateStepCounter(data) {
    const counter = document.getElementById("step-counter");
    if (!counter) return;

    if (data && typeof data.n === "number" && typeof data.m === "number") {
        counter.textContent = `Step ${data.n} of ${data.m}`;
    }
}

/**
 * Reset the step counter to its idle state.
 */
function resetStepCounter() {
    const counter = document.getElementById("step-counter");
    if (counter) counter.textContent = "Ready";
}

// -----------------------------------------------------------------------------
// SECTION 4 — User Input
// -----------------------------------------------------------------------------

/**
 * Append the user's prompt to the log as a styled message.
 *
 * @param {string} prompt
 * @param {HTMLElement} log
 */
function appendUserMessage(prompt, log) {
    const userDiv = document.createElement("div");
    userDiv.className = "user-msg";
    userDiv.innerHTML = `<strong>> User:</strong> ${prompt}`;
    log.appendChild(userDiv);
}

// -----------------------------------------------------------------------------
// SECTION 5 — Image Attach
// -----------------------------------------------------------------------------

const imageInput = document.getElementById("imageInput");
const attachBtn = document.getElementById("attachBtn");
const imagePreviewWrap = document.getElementById("imagePreviewWrap");
const imagePreview = document.getElementById("imagePreview");
const clearImageBtn = document.getElementById("clearImageBtn");
const stopBtn = document.getElementById("stopBtn");

let attachedFile = null;
let currentStream = null;

function setAttachedFile(file) {
    attachedFile = file;
    if (file) {
        imagePreview.src = URL.createObjectURL(file);
        imagePreviewWrap.style.display = "flex";
        attachBtn.classList.add("has-image");
    } else {
        imagePreview.src = "";
        imagePreviewWrap.style.display = "none";
        attachBtn.classList.remove("has-image");
        imageInput.value = "";
    }
}

attachBtn.addEventListener("click", () => imageInput.click());
imageInput.addEventListener("change", () => setAttachedFile(imageInput.files?.[0] ?? null));
clearImageBtn.addEventListener("click", () => setAttachedFile(null));
// Wired here rather than via an inline onclick: app.js is loaded as a module,
// so its functions are not globals the HTML can reference.
stopBtn.addEventListener("click", () => stopGeneration());

// -----------------------------------------------------------------------------
// SECTION 5b — Voice recorder
// The audio is sent to the server in 30 s chunks while recording, so a closed tab
// loses seconds, not the whole recording. When it stops, the server transcribes it
// (progress is streamed) and the user chooses what the text is:
//   - an instruction: it goes into the prompt box;
//   - a text to process: it stays a file (transcripts/…), attached to the next prompt.
// -----------------------------------------------------------------------------

const micBtn = document.getElementById("micBtn");
const voiceCard = document.getElementById("voiceCard");
const voiceStatus = document.getElementById("voiceStatus");
const voiceText = document.getElementById("voiceText");
const voiceActions = document.getElementById("voiceActions");
const textChipWrap = document.getElementById("textChipWrap");
const textChip = document.getElementById("textChip");

const CHUNK_MS = 30_000;
let recorder = null;
let timerId = null;
let attachedTextPath = null;   // transcripts/….txt attached to the next prompt
let voiceResult = null;        // { path, text } shown in the card

if (!navigator.mediaDevices?.getUserMedia || !window.MediaRecorder) micBtn.style.display = "none";

function showVoice(message, { error = false, text = null } = {}) {
    voiceCard.style.display = "block";
    voiceStatus.textContent = message;
    voiceStatus.classList.toggle("error", error);
    voiceText.style.display = text === null ? "none" : "block";
    voiceText.textContent = text ?? "";
    voiceActions.style.display = text === null ? "none" : "flex";
}

function setAttachedText(path) {
    attachedTextPath = path;
    textChipWrap.style.display = path ? "flex" : "none";
    textChip.textContent = path ? `📝 ${path}` : "";
}

function pickMimeType() {
    const candidates = ["audio/webm;codecs=opus", "audio/webm", "audio/mp4", "audio/ogg;codecs=opus"];
    return candidates.find((m) => MediaRecorder.isTypeSupported(m)) ?? "";
}

async function uploadChunk(rec, blob) {
    if (rec.error) return;
    try {
        const response = await fetch(`/api/audio/${rec.id}`, { method: "POST", body: blob });
        if (!response.ok) throw new Error((await response.text()) || response.statusText);
    } catch (err) {
        rec.error = err.message;
        if (recorder && recorder.state === "recording") recorder.stop();
    }
}

async function startRecording() {
    let stream;
    try {
        stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch (err) {
        showVoice(`Microphone unavailable: ${err.message}`, { error: true });
        return;
    }

    const mimeType = pickMimeType();
    const mediaRecorder = new MediaRecorder(stream, {
        ...(mimeType ? { mimeType } : {}),
        audioBitsPerSecond: 32000,   // plenty for speech: about 14 MB per hour
    });

    let rec;
    try {
        const response = await fetch(
            `/api/audio/start?type=${encodeURIComponent(mediaRecorder.mimeType || mimeType || "audio/webm")}`,
            { method: "POST" });
        if (!response.ok) throw new Error((await response.text()) || response.statusText);
        rec = { ...(await response.json()), queue: Promise.resolve(), error: null };
    } catch (err) {
        stream.getTracks().forEach((t) => t.stop());
        showVoice(`Cannot start the recording: ${err.message}`, { error: true });
        return;
    }

    mediaRecorder.ondataavailable = (e) => {
        if (e.data.size > 0) rec.queue = rec.queue.then(() => uploadChunk(rec, e.data));
    };
    mediaRecorder.onstop = () => {
        stream.getTracks().forEach((t) => t.stop());
        clearInterval(timerId);
        recorder = null;
        micBtn.classList.remove("recording");
        finishRecording(rec);
    };

    recorder = mediaRecorder;
    mediaRecorder.start(CHUNK_MS);
    micBtn.classList.add("recording");
    const startedAt = Date.now();
    const tick = () => {
        const s = Math.floor((Date.now() - startedAt) / 1000);
        const mm = String(Math.floor(s / 60)).padStart(2, "0");
        const ss = String(s % 60).padStart(2, "0");
        showVoice(`● Recording ${mm}:${ss} — click 🎙 to stop`);
    };
    tick();
    timerId = setInterval(tick, 1000);
}

async function finishRecording(rec) {
    micBtn.classList.add("busy");
    try {
        await rec.queue;
        if (rec.error) {
            showVoice(`Upload interrupted: ${rec.error}. What was recorded is in ${rec.path}.`, { error: true });
            return;
        }
        await transcribeRecording(rec);
    } finally {
        micBtn.classList.remove("busy");
    }
}

async function transcribeRecording(rec) {
    showVoice("Transcribing…");
    const lang = (navigator.language || "").split("-")[0];
    let response;
    try {
        response = await fetch(`/api/audio/${rec.id}/transcribe?lang=${encodeURIComponent(lang)}`, { method: "POST" });
        if (!response.ok) throw new Error((await response.text()) || response.statusText);
    } catch (err) {
        showVoice(`Transcription failed: ${err.message}. The audio is in ${rec.path}.`, { error: true });
        return;
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        const events = buffer.split("\n\n");
        buffer = events.pop();
        for (const event of events) {
            const line = event.split("\n").find((l) => l.startsWith("data:"));
            if (!line) continue;
            const message = JSON.parse(line.slice(5).trim());
            if (message.type === "progress") {
                showVoice(message.total > 1
                    ? `Transcribing… part ${message.part}/${message.total}`
                    : "Transcribing…");
            } else if (message.type === "error") {
                showVoice(`Transcription failed: ${message.message} The audio is in ${rec.path}.`, { error: true });
                return;
            } else if (message.type === "done") {
                voiceResult = { path: message.path, text: message.text };
                const words = message.text.trim().split(/\s+/).length;
                showVoice(`${words} words — saved as ${message.path}`, { text: message.text });
                return;
            }
        }
    }
}

micBtn.addEventListener("click", () => {
    if (recorder) recorder.stop();
    else startRecording();
});

document.getElementById("voiceUseBtn").addEventListener("click", () => {
    const input = document.getElementById("in");
    input.value = voiceResult.text;
    voiceCard.style.display = "none";
    input.focus();
});
document.getElementById("voiceKeepBtn").addEventListener("click", () => {
    setAttachedText(voiceResult.path);
    voiceCard.style.display = "none";
    document.getElementById("in").focus();
});
document.getElementById("voiceCloseBtn").addEventListener("click", () => {
    voiceCard.style.display = "none";
});
document.getElementById("clearTextBtn").addEventListener("click", () => setAttachedText(null));

// -----------------------------------------------------------------------------
// SECTION 6 — SSE Stream (GET for text-only, POST for image)
// -----------------------------------------------------------------------------

/**
 * Shared SSE message handler. Returns true when the stream should be closed.
 */
function handleSseMessage(message, log) {
    if (message.type === "step") {
        updateStepCounter(message.data);
        return false;
    }
    appendMessageToLog(message, log);
    scrollToBottom(log);
    return message.type === "done" || message.type === "error" || message.type === "danger";
}

/**
 * Text-only path: GET /api/chat via EventSource.
 */
function startChatStreamGet(prompt, log) {
    const url = `/api/chat?prompt=${encodeURIComponent(prompt)}`;
    const stream = new EventSource(url);

    stream.onmessage = function (event) {
        const message = JSON.parse(event.data);
        if (handleSseMessage(message, log)) {
            resetStepCounter();
            stream.close();
            currentStream = null;
            setGenerating(false);
        }
    };

    stream.onerror = function () {
        console.error("SSE connection error — closing stream.");
        resetStepCounter();
        stream.close();
        currentStream = null;
        setGenerating(false);
    };

    return stream;
}

/**
 * Image path: POST /api/chat via fetch + ReadableStream.
 * EventSource only supports GET, so we use fetch for multipart POST.
 */
function startChatStreamPost(prompt, imageFile, log) {
    const controller = new AbortController();

    const form = new FormData();
    form.append("prompt", prompt);
    form.append("image", imageFile, imageFile.name);

    (async () => {
        let response;
        try {
            response = await fetch("/api/chat", {
                method: "POST",
                body: form,
                signal: controller.signal,
            });
        } catch (err) {
            if (err.name !== "AbortError") {
                console.error("Fetch error:", err);
            }
            resetStepCounter();
            currentStream = null;
            setGenerating(false);
            return;
        }

        if (!response.ok) {
            const text = await response.text().catch(() => response.statusText);
            const errDiv = document.createElement("div");
            errDiv.className = "danger";
            errDiv.textContent = `✗ Server error ${response.status}: ${text}`;
            log.appendChild(errDiv);
            scrollToBottom(log);
            resetStepCounter();
            currentStream = null;
            setGenerating(false);
            return;
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";

        while (true) {
            let done, value;
            try { ({ done, value } = await reader.read()); }
            catch (err) {
                if (err.name !== "AbortError") console.error("Stream read error:", err);
                break;
            }
            if (done) break;

            buffer += decoder.decode(value, { stream: true });
            const parts = buffer.split("\n\n");
            buffer = parts.pop();

            for (const part of parts) {
                for (const line of part.split("\n")) {
                    if (!line.startsWith("data:")) continue;
                    let message;
                    try { message = JSON.parse(line.slice(5).trim()); } catch { continue; }
                    if (handleSseMessage(message, log)) {
                        resetStepCounter();
                        currentStream = null;
                        setGenerating(false);
                        return;
                    }
                }
            }
        }

        if (currentStream !== null) {
            resetStepCounter();
            currentStream = null;
            setGenerating(false);
        }
    })();

    return { close: () => controller.abort() };
}

function setGenerating(active) {
    stopBtn.disabled = !active;
}

function stopGeneration() {
    if (!currentStream) return;
    currentStream.close();
    currentStream = null;
    resetStepCounter();
    setGenerating(false);
    const log = document.getElementById("log");
    const div = document.createElement("div");
    div.className = "warning";
    div.textContent = "⚠ Generation stopped by user.";
    log.appendChild(div);
    scrollToBottom(log);
}

// -----------------------------------------------------------------------------
// SECTION 7 — Main Entry Point + Keyboard Wiring
// -----------------------------------------------------------------------------

/**
 * Main entry point — called on Enter or button click.
 */
function run() {
    const input = document.getElementById("in");
    const prompt = input.value.trim();
    if (!prompt || currentStream) return;

    const log = document.getElementById("log");

    // The agent is told about an attached transcript by its path; what it contains is data.
    const textPath = attachedTextPath;
    const sent = textPath ? `${prompt}\n\n[Attached text file: ${textPath}]` : prompt;

    // Show attachment indicators in log
    if (attachedFile || textPath) {
        const div = document.createElement("div");
        div.className = "user-msg";
        const marks = (attachedFile ? `📎 [${attachedFile.name}] ` : "") + (textPath ? `📝 [${textPath}] ` : "");
        div.innerHTML = `<strong>> User:</strong> ${marks}`;
        div.appendChild(document.createTextNode(prompt));
        log.appendChild(div);
    } else {
        appendUserMessage(prompt, log);
    }

    const imageFile = attachedFile;
    setAttachedFile(null);
    setAttachedText(null);
    input.value = "";
    scrollToBottom(log);

    setGenerating(true);
    currentStream = imageFile
        ? startChatStreamPost(sent, imageFile, log)
        : startChatStreamGet(sent, log);
}

// Command history state
const cmdHistory = [];
let histIndex = -1;

// Single keydown listener — history push must happen before run() clears the input
document.getElementById("in").addEventListener("keydown", function (e) {
    if (e.key === "ArrowUp") {
        e.preventDefault();
        if (histIndex < cmdHistory.length - 1) {
            histIndex++;
            this.value = cmdHistory[histIndex];
        }
    } else if (e.key === "ArrowDown") {
        e.preventDefault();
        if (histIndex > 0) {
            histIndex--;
            this.value = cmdHistory[histIndex];
        } else {
            histIndex = -1;
            this.value = "";
        }
    } else if (e.key === "Enter" && !e.shiftKey) {
        const prompt = this.value.trim();
        if (prompt) {
            cmdHistory.unshift(prompt); // push before run() clears the field
            if (cmdHistory.length > 50) cmdHistory.pop();
            histIndex = -1;
        }
        run();
    } else if (e.key === "Escape") {
        stopGeneration();
    }
});

// Initialise stop button state
setGenerating(false);