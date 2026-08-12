const cfg = window.CONFIG;
const $ = (id) => document.getElementById(id);

// --- Cognito login, authorization code + PKCE, no libraries ---

const base64url = (bytes) =>
  btoa(String.fromCharCode(...new Uint8Array(bytes)))
    .replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");

async function login() {
  const verifier = base64url(crypto.getRandomValues(new Uint8Array(32)));
  sessionStorage.verifier = verifier;
  const challenge = base64url(
    await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier)));
  location.href = `${cfg.hostedUiBase}/oauth2/authorize?` + new URLSearchParams({
    client_id: cfg.clientId,
    response_type: "code",
    scope: "openid profile",
    redirect_uri: cfg.redirectUri,
    code_challenge_method: "S256",
    code_challenge: challenge,
  });
}

async function exchangeCode(code) {
  const response = await fetch(`${cfg.hostedUiBase}/oauth2/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "authorization_code",
      client_id: cfg.clientId,
      code,
      redirect_uri: cfg.redirectUri,
      code_verifier: sessionStorage.verifier,
    }),
  });
  const tokens = await response.json();
  sessionStorage.accessToken = tokens.access_token;
  history.replaceState(null, "", location.pathname);
}

const claims = () => {
  try { return JSON.parse(atob(sessionStorage.accessToken.split(".")[1])); }
  catch { return null; }
};

// --- Chat, with a transcript that survives the consent redirect ---

const transcript = JSON.parse(sessionStorage.transcript ?? "[]");

function append(role, text, save = true) {
  const div = document.createElement("div");
  div.className = `msg ${role}`;
  // DOM construction only, agent output is untrusted; consent links must
  // navigate this tab, since the grant binds to the session kept here
  for (const part of text.split(/(https:\/\/\S+)/)) {
    if (part.startsWith("https://")) {
      const a = document.createElement("a");
      a.href = part;
      a.textContent = part.includes("/identities/oauth2/authorize")
        ? "Grant the agent access →"
        : part;
      div.appendChild(a);
    } else if (part) {
      div.appendChild(document.createTextNode(part));
    }
  }
  $("chat").appendChild(div);
  div.scrollIntoView();
  if (save) {
    transcript.push([role, text]);
    sessionStorage.transcript = JSON.stringify(transcript);
  }
  return div;
}

function appendPending() {
  const div = document.createElement("div");
  div.className = "msg agent pending";
  for (let i = 0; i < 3; i++)
    div.appendChild(document.createElement("span"));
  $("chat").appendChild(div);
  div.scrollIntoView();
  return div;
}

// InvokeAgentRuntime requires a session id of at least 33 characters; the
// runtime routes requests with the same id to the same microVM. It must
// survive reloads: the consent redirect reloads the page, and the granted
// token is found via the session that requested it
const sessionId = sessionStorage.sessionId ??=
  crypto.randomUUID() + "-" + crypto.randomUUID();

async function send(prompt) {
  append("you", prompt);
  const pending = appendPending();
  try {
    const response = await fetch(
      `/runtimes/${encodeURIComponent(cfg.agentArn)}/invocations?qualifier=DEFAULT`, {
        method: "POST",
        headers: {
          Authorization: `Bearer ${sessionStorage.accessToken}`,
          "Content-Type": "application/json",
          "X-Amzn-Bedrock-AgentCore-Runtime-Session-Id": sessionId,
        },
        body: JSON.stringify({ prompt }),
      });
    if (response.status === 401 || response.status === 403) {
      sessionStorage.removeItem("accessToken");
      append("agent", "Your session has expired. Reloading to log in again...");
      setTimeout(login, 1500);
      return;
    }
    if (!response.ok) {
      const body = await response.text();
      append("agent", `The agent call failed (HTTP ${response.status}): ${body.slice(0, 300)}`);
      return;
    }
    const data = await response.json();
    const message = data.message ?? JSON.stringify(data);
    // A consent link means this prompt is waiting on access; replay it
    // automatically once the grant completes
    if (message.includes("/identities/oauth2/authorize"))
      sessionStorage.pendingPrompt = prompt;
    append("agent", message);
  } catch (error) {
    append("agent", `The agent call failed: ${error}`);
  } finally {
    pending.remove();
  }
}

// --- Wire-up ---

const params = new URLSearchParams(location.search);
if (params.get("code")) await exchangeCode(params.get("code"));
if (!sessionStorage.accessToken) await login();

$("splash").hidden = true;
$("chat").hidden = false;
$("form").hidden = false;
$("who").textContent = claims()?.username ?? "";

for (const [role, text] of transcript) append(role, text, false);
if (!transcript.length)
  append("agent",
    "Hi! I can list, submit, and approve expenses on your behalf. What do you need?");

// Landing back from a consent redirect: prove to AgentCore Identity that
// the user who consented is the user logged in here, so it stores the token
const bindingSession = params.get("session_id");
if (bindingSession) {
  const response = await fetch("/oauth/complete", {
    method: "POST",
    headers: {
      Authorization: `Bearer ${sessionStorage.accessToken}`,
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ sessionId: bindingSession }),
  });
  history.replaceState(null, "", location.pathname);
  if (response.ok) {
    append("agent", "Access granted!");
    const prompt = sessionStorage.pendingPrompt;
    delete sessionStorage.pendingPrompt;
    if (prompt) send(prompt);
  } else {
    append("agent", `Completing the grant failed (HTTP ${response.status}).`);
  }
}

$("form").addEventListener("submit", (event) => {
  event.preventDefault();
  const prompt = $("prompt").value.trim();
  if (!prompt) return;
  $("prompt").value = "";
  send(prompt);
});
