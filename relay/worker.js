// QuackSnap relay — Cloudflare Worker.
//
// Zero-knowledge by construction: blobs are AES-GCM ciphertext sealed to the
// recipient's device certificate, and the push payload carries only the E2EE
// envelope. This worker stores bytes with a TTL and forwards pushes to APNs.
//
// Bindings (wrangler.toml):
//   BLOBS   R2 bucket for ciphertext (10-minute lifecycle rule recommended)
//   TOKENS  KV namespace mapping deviceId → APNs device token
// Secrets (wrangler secret put ...):
//   APNS_TEAM_ID, APNS_KEY_ID, APNS_PRIVATE_KEY (p8 PEM), APNS_TOPIC (bundle id)

const BLOB_TTL_SECONDS = 600;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const path = url.pathname;

    if (request.method === "PUT" && path.startsWith("/v1/blob/")) {
      const id = path.slice("/v1/blob/".length);
      if (!/^[A-Za-z0-9_-]{16,64}$/.test(id)) return json({ error: "bad id" }, 400);
      await env.BLOBS.put(id, request.body, {
        customMetadata: { expiresAt: String(Date.now() + BLOB_TTL_SECONDS * 1000) },
      });
      return json({ ok: true, id });
    }

    if (request.method === "GET" && path.startsWith("/v1/blob/")) {
      const id = path.slice("/v1/blob/".length);
      const object = await env.BLOBS.get(id);
      if (!object) return json({ error: "not found" }, 404);
      const expiresAt = Number(object.customMetadata?.expiresAt ?? 0);
      if (expiresAt && Date.now() > expiresAt) {
        await env.BLOBS.delete(id);
        return json({ error: "expired" }, 404);
      }
      return new Response(object.body, {
        headers: { "content-type": "application/octet-stream" },
      });
    }

    if (request.method === "POST" && path === "/v1/register") {
      const { deviceId, token } = await request.json();
      if (!deviceId || !token) return json({ error: "deviceId and token required" }, 400);
      await env.TOKENS.put(deviceId, token);
      return json({ ok: true });
    }

    if (request.method === "POST" && path === "/v1/push") {
      const { to, payload } = await request.json();
      const token = await env.TOKENS.get(to);
      if (!token) return json({ error: "unknown device" }, 404);

      const jwt = await apnsJwt(env);
      const response = await fetch(`https://api.push.apple.com/3/device/${token}`, {
        method: "POST",
        headers: {
          authorization: `bearer ${jwt}`,
          "apns-topic": env.APNS_TOPIC,
          "apns-push-type": "alert",
          "apns-priority": "10",
        },
        body: JSON.stringify(payload),
      });
      if (!response.ok) {
        return json({ error: "apns", status: response.status, body: await response.text() }, 502);
      }
      return json({ ok: true });
    }

    return json({ error: "not found" }, 404);
  },
};

function json(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

let cachedJwt = { token: null, issuedAt: 0 };

async function apnsJwt(env) {
  // APNs accepts tokens 20–60 minutes old; refresh every 40.
  if (cachedJwt.token && Date.now() - cachedJwt.issuedAt < 40 * 60 * 1000) {
    return cachedJwt.token;
  }
  const header = b64url(JSON.stringify({ alg: "ES256", kid: env.APNS_KEY_ID }));
  const claims = b64url(JSON.stringify({ iss: env.APNS_TEAM_ID, iat: Math.floor(Date.now() / 1000) }));
  const unsigned = `${header}.${claims}`;

  const pem = env.APNS_PRIVATE_KEY.replace(/-----[^-]+-----|\s/g, "");
  const key = await crypto.subtle.importKey(
    "pkcs8",
    Uint8Array.from(atob(pem), (c) => c.charCodeAt(0)),
    { name: "ECDSA", namedCurve: "P-256" },
    false,
    ["sign"],
  );
  const signature = await crypto.subtle.sign(
    { name: "ECDSA", hash: "SHA-256" },
    key,
    new TextEncoder().encode(unsigned),
  );
  cachedJwt = { token: `${unsigned}.${b64url(signature)}`, issuedAt: Date.now() };
  return cachedJwt.token;
}

function b64url(input) {
  const bytes = typeof input === "string" ? new TextEncoder().encode(input) : new Uint8Array(input);
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}
