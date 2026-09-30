async function register() {

    let username = document.getElementById("username").value;
    let password = document.getElementById("password").value;
    let repassword = document.getElementById("repassword").value;

    let message = document.getElementById("message");

    // Check username
    if (username.length <= 3 || username.length >= 15) {
        message.textContent = "Username must be 4-14 characters.";
        return;
    }

    // Check password
    if (password.length === 0) {
        message.textContent = "Please write a password.";
        return;
    }

    if (password.length <= 7 || password.length >= 16) {
        message.textContent = "Password must be 8-15 characters.";
        return;
    }

    // Check passwords
    if (password !== repassword) {
        message.textContent = "Passwords do not match.";
        return;
    }

    // Send data to ASP.NET
    const response = await fetch("/api/register", {
        method: "POST",

        headers: {
            "Content-Type": "application/json"
        },

        body: JSON.stringify({
            username: username,
            password: password
        })
    });

    // Read server response
    const result = await response.text();

    // Show server message
    message.textContent = result;

    if (response.ok) {
    window.location.href = "/home.html";
    } else {
        message.textContent = result;
    }
}


// Reads a server message whether it is a JSON string, {error: ...}, or empty (429).
async function readMessage(res) {
    const text = await res.text();
    if (!text) return res.status === 429 ? "Too many attempts. Try again in a minute." : "Request failed.";
    try {
        const v = JSON.parse(text);
        return typeof v === "string" ? v : (v.error ?? text);
    } catch {
        return text;
    }
}

async function login() {
    const message = document.getElementById("message");
    const username = document.getElementById("username").value;
    const password = document.getElementById("password").value;

    try {
        const res = await fetch("/api/login", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ username, password })
        });

        if (res.ok) {
            window.location.href = "/home.html";
            return;
        }

        message.textContent = await readMessage(res);
    } catch {
        message.textContent = "Network error. Please try again.";
    }
}


async function logout() {
    const message = document.getElementById("message");

    try {
        const res = await fetch("/api/logout", { method: "POST" });

        if (res.ok) {
            window.location.href = "/";
            return;
        }

        message.textContent = await readMessage(res);
    } catch {
        message.textContent = "Network error. Please try again.";
    }
}


// Shared fetch helper: JSON in, JSON out, clean error messages.
// Returns { ok, status, data } on success or { ok: false, status, message } on failure.
// A 401 sends the user to "/" (session expired or logged out elsewhere).
// Pass redirectOn401: false for calls where 401 is an expected answer (e.g. wrong password).
// Network failures throw, so callers wrap it in try/catch.
async function api(url, { method = "GET", body, redirectOn401 = true } = {}) {
    const init = { method, headers: {} };
    if (body !== undefined) {
        init.headers["Content-Type"] = "application/json";
        init.body = JSON.stringify(body);
    }

    const res = await fetch(url, init);

    if (res.status === 401 && redirectOn401) {
        window.location.href = "/";
        return { ok: false, status: 401, message: "Not authenticated." };
    }

    if (!res.ok) {
        return { ok: false, status: res.status, message: await readMessage(res) };
    }

    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    return { ok: true, status: res.status, data };
}

// home.html: show who is logged in (textContent only, never innerHTML)
async function loadMe() {
    const greeting = document.getElementById("greeting");
    const message = document.getElementById("message");

    try {
        const res = await api("/api/me");
        if (!res.ok) {
            if (res.status !== 401) message.textContent = res.message;   // 401 already redirected
            return;
        }
        greeting.textContent = "Hello, " + res.data.username;
    } catch {
        message.textContent = "Network error. Please try again.";
    }
}

// Run automatically on pages that have the greeting element (no inline script needed)
if (document.getElementById("greeting")) loadMe();
