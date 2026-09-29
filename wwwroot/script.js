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
