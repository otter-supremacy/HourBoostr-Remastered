const browserRoutes = new Map([
  ["/dashboard", ["GET", "HEAD"]],
  ["/login", ["GET", "HEAD"]],
  ["/signup", ["GET", "HEAD"]],
  ["/reset", ["GET", "HEAD"]],
  ["/members.css", ["GET", "HEAD"]],
  ["/members.js", ["GET", "HEAD"]],
  ["/member-chat.js", ["GET", "HEAD"]],
  ["/auth/google", ["GET"]],
  ["/auth/callback", ["GET"]],
  ["/auth/password/register", ["POST"]],
  ["/auth/password/login", ["POST"]],
  ["/auth/password/recover", ["POST"]],
  ["/api/member/config", ["GET"]],
  ["/api/member/me", ["GET"]],
  ["/api/member/appearance", ["GET", "PUT"]],
  ["/api/member/games", ["GET"]],
  ["/api/member/chat", ["GET", "PUT"]],
  ["/api/member/chat/key/gpt", ["PUT", "DELETE"]],
  ["/api/member/chat/key/qwen", ["PUT", "DELETE"]],
  ["/api/member/chat/preview", ["POST"]],
  ["/api/member/chat/regulars", ["GET"]],
  ["/api/member/chat/regulars/approve", ["POST"]],
  ["/api/member/steam/qr", ["GET"]],
  ["/api/member/steam/link", ["POST", "DELETE"]],
  ["/api/member/game", ["PUT"]],
  ["/api/member/start", ["POST"]],
  ["/api/member/pause", ["POST"]],
  ["/api/member/logout", ["POST"]]
]);

const publicAssets = new Set([
  "/", "/site.css", "/site.js", "/favicon.ico", "/robots.txt", "/sitemap.xml", "/feed.xml",
  "/assets/dashboard.webp", "/assets/statistics.webp", "/assets/library.webp", "/assets/schedule.webp",
  "/assets/afk.webp", "/assets/dashboard-share.jpg"
]);
const privacy = `<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Privacy | HourBoostr Members</title><style>body{max-width:42rem;margin:3rem auto;padding:0 1rem;font:1rem/1.65 system-ui}</style><h1>HourBoostr Members privacy</h1><p>Google sign-in uses your verified email address and Google account identifier. Email and password signup stores the email address you provide. HourBoostr stores password and recovery-code hashes and uses the recovery code you saved to reset a password. It does not send verification or recovery emails.</p><p>Cloudflare serves the website. The HourBoostr server stores account settings, browser sessions, and moderation records. It uses first-party cookies for sign-in. The public preview uses simulated Steam. It does not request a Steam password or send Steam messages.</p><p>Optional AI previews send the sample text you enter to Groq using your own API key. HourBoostr stores saved Groq keys encrypted and lets you remove them from chat settings.</p><p>For privacy questions or account removal, contact the maintainer through the support email displayed on the Google sign-in consent screen. Keep private data out of public issue reports.</p><p><a href="/">Back to HourBoostr</a></p></html>`;

export default {
  async fetch(request, env) {
    const browser = new URL(request.url);
    if (browser.hostname !== env.PUBLIC_HOST) return new Response("Not found", { status: 404 });
    if (env.REDIRECT_TO) {
      const isPage = request.method === "GET" || request.method === "HEAD";
      if (!isPage || !["/", "/privacy", "/auth/google", "/auth/callback", "/login", "/signup", "/reset", "/dashboard"].includes(browser.pathname))
        return new Response("Not found", { status: 404 });
      const destination = new URL(browser.pathname.startsWith("/auth/") ? "/login" : browser.pathname, env.REDIRECT_TO);
      return new Response(null, { status: 302, headers: {
        Location: destination.href, "Cache-Control": "no-store", "Referrer-Policy": "no-referrer",
      } });
    }
    if (browser.pathname === "/privacy" && request.method === "GET")
      return new Response(privacy, { headers: { "Content-Type": "text/html; charset=utf-8", "Referrer-Policy": "no-referrer" } });
    const guide = /^\/guides(?:\/[a-z0-9]+(?:-[a-z0-9]+)*)?\/?$/.test(browser.pathname);
    if ((request.method === "GET" || request.method === "HEAD") && (publicAssets.has(browser.pathname) || guide) && env.ASSETS) {
      if (guide && !browser.pathname.endsWith("/"))
        return Response.redirect(new URL(browser.pathname + "/", browser), 308);
      const asset = await env.ASSETS.fetch(new Request(browser, { method: request.method }));
      const headers = new Headers(asset.headers);
      headers.set("X-Content-Type-Options", "nosniff");
      headers.set("Referrer-Policy", "strict-origin-when-cross-origin");
      if (browser.pathname === "/feed.xml") headers.set("Content-Type", "application/rss+xml; charset=utf-8");
      if (browser.pathname === "/" || guide) {
        headers.set("Content-Security-Policy", "default-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
      }
      return new Response(asset.body, { status: asset.status, headers });
    }
    if (!browserRoutes.get(browser.pathname)?.includes(request.method))
      return new Response("Not found", { status: 404 });
    if (env.REGISTRATION_OPEN !== "1" && browser.pathname !== "/api/member/config") return new Response("Registration is being prepared.", { status: 503 });
    if (!env.ORIGIN_KEY) return new Response("Members is temporarily unavailable.", { status: 503 });

    const upstream = new URL(env.MEMBER_ORIGIN);
    upstream.pathname = "/__hbr_members" + browser.pathname;
    upstream.search = browser.search;
    const headers = new Headers();
    for (const name of ["Accept", "Content-Type", "Cookie", "Origin", "X-HBR-Member"]) {
      const value = request.headers.get(name);
      if (value) headers.set(name, value);
    }
    headers.set("X-HBR-Origin-Key", env.ORIGIN_KEY);
    headers.set("X-HBR-Edge-IP", request.headers.get("CF-Connecting-IP") ?? "");
    try {
      const response = await fetch(upstream, {
        method: request.method, headers, body: request.body, redirect: "manual",
        cf: { cacheTtl: 0 },
      });
      if (response.status >= 500)
        return new Response("Members is temporarily unavailable.", { status: 503 });
      const safeHeaders = new Headers(response.headers);
      safeHeaders.set("Cache-Control", "private, no-store");
      safeHeaders.set("Referrer-Policy", "no-referrer");
      safeHeaders.set("X-Robots-Tag", "noindex");
      return new Response(response.body, { status: response.status, headers: safeHeaders });
    } catch {
      return new Response("Members is temporarily unavailable.", { status: 503 });
    }
  },
};
