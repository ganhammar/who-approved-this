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

// --- Chat against the agent, same-origin via CloudFront ---

// InvokeAgentRuntime requires a session id of at least 33 characters; the
// runtime routes requests with the same id to the same microVM. It must
// survive reloads: the consent redirect reloads the page, and the granted
// token is found via the session that requested it
const sessionId = sessionStorage.sessionId ??=
  crypto.randomUUID() + "-" + crypto.randomUUID();

function append(role, text) {
  const div = document.createElement("div");
  div.className = `msg ${role}`;
  // Make consent links from the agent clickable; DOM construction only, the
  // agent's output is untrusted
  for (const part of text.split(/(https:\/\/\S+)/)) {
    if (part.startsWith("https://")) {
      // Consent links must navigate this tab: the granted token is bound to
      // the runtime session, and the session id lives in sessionStorage
      const a = document.createElement("a");
      a.href = part;
      a.textContent = part;
      div.appendChild(a);
    } else if (part) {
      div.appendChild(document.createTextNode(part));
    }
  }
  $("chat").appendChild(div);
  div.scrollIntoView();
  return div;
}

async function send(prompt) {
  append("you", prompt);
  const pending = append("agent", "Thinking (cold starts can take a while)...");
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
    append("agent", data.message ?? JSON.stringify(data));
  } catch (error) {
    append("agent", `The agent call failed: ${error}`);
  } finally {
    pending.remove();
  }
}

// --- Wire-up ---

const code = new URLSearchParams(location.search).get("code");
if (code) await exchangeCode(code);
if (!sessionStorage.accessToken) await login();

$("who").textContent = claims()?.username ?? "";
append("agent",
  "Hi! I can list, submit, and approve expenses on your behalf. What do you need?");

$("form").addEventListener("submit", (event) => {
  event.preventDefault();
  const prompt = $("prompt").value.trim();
  if (!prompt) return;
  $("prompt").value = "";
  send(prompt);
});
