import { Hono } from "hono";
import { cors } from "hono/cors";
import { ClipSyncRoom, type Env } from "./room.js";

export { ClipSyncRoom };

const app = new Hono<{ Bindings: Env }>();

app.use("*", cors());

// Normalize paths with redundant slashes (e.g. //v1 -> /v1)
app.use("*", async (c, next) => {
  const url = new URL(c.req.url);
  if (url.pathname.includes("//")) {
    const cleanPath = url.pathname.replace(/\/+/g, "/");
    const cleanUrl = new URL(cleanPath + url.search, url.origin);
    return app.fetch(new Request(cleanUrl, c.req.raw), c.env, c.executionCtx);
  }
  await next();
});

// Health check
app.get("/health", (c) => {
  return c.json({
    ok: true,
    service: "clipsync-cloudflare-worker",
    version: "1.0.0",
    protocolVersion: 1,
    timestamp: new Date().toISOString(),
  });
});

// Helper to get group DO stub (default room for Phase 1)
function getRoomStub(c: any, groupId = "clipsync-default-group") {
  const id = c.env.CLIPSYNC_ROOM.idFromName(groupId);
  return c.env.CLIPSYNC_ROOM.get(id);
}

// WebSocket live event stream (/v1/events?token=...)
app.get("/v1/events", (c) => {
  const stub = getRoomStub(c);
  return stub.fetch(c.req.raw);
});

// Device Registration
app.post("/v1/devices/register", async (c) => {
  const stub = getRoomStub(c);
  const internalReq = new Request("http://internal/internal/device/register", {
    method: "POST",
    headers: c.req.raw.headers,
    body: await c.req.raw.text(),
  });
  return stub.fetch(internalReq);
});

// Create Pairing Session
app.post("/v1/auth/pairing/session", async (c) => {
  const stub = getRoomStub(c);
  const headers = new Headers(c.req.raw.headers);
  headers.set("x-public-origin", new URL(c.req.url).origin);
  const internalReq = new Request("http://internal/internal/auth/pairing/session", {
    method: "POST",
    headers,
  });
  return stub.fetch(internalReq);
});

// Claim Pairing Session
app.post("/v1/auth/pair", async (c) => {
  const stub = getRoomStub(c);
  const internalReq = new Request("http://internal/internal/auth/pair", {
    method: "POST",
    headers: c.req.raw.headers,
    body: await c.req.raw.text(),
  });
  return stub.fetch(internalReq);
});

// List Devices
app.get("/v1/devices", (c) => {
  const stub = getRoomStub(c);
  const internalReq = new Request("http://internal/internal/devices", {
    method: "GET",
    headers: c.req.raw.headers,
  });
  return stub.fetch(internalReq);
});

// Revoke Device
app.delete("/v1/devices/:deviceId", (c) => {
  const stub = getRoomStub(c);
  const deviceId = c.req.param("deviceId");
  const internalReq = new Request(`http://internal/internal/devices/${encodeURIComponent(deviceId)}`, {
    method: "DELETE",
    headers: c.req.raw.headers,
  });
  return stub.fetch(internalReq);
});

// Query History
app.get("/v1/history", (c) => {
  const stub = getRoomStub(c);
  const url = new URL(c.req.url);
  const internalReq = new Request(`http://internal/internal/history${url.search}`, {
    method: "GET",
    headers: c.req.raw.headers,
  });
  return stub.fetch(internalReq);
});

// Delete History Item
app.delete("/v1/history/:itemId", (c) => {
  const stub = getRoomStub(c);
  const itemId = c.req.param("itemId");
  const internalReq = new Request(`http://internal/internal/history/${encodeURIComponent(itemId)}`, {
    method: "DELETE",
    headers: c.req.raw.headers,
  });
  return stub.fetch(internalReq);
});

// Clear History
app.post("/v1/history/clear", (c) => {
  const stub = getRoomStub(c);
  const internalReq = new Request("http://internal/internal/history/clear", {
    method: "POST",
    headers: c.req.raw.headers,
  });
  return stub.fetch(internalReq);
});

// HTTP Sync submission fallback
app.post("/v1/sync", async (c) => {
  const stub = getRoomStub(c);
  const internalReq = new Request("http://internal/internal/sync", {
    method: "POST",
    headers: c.req.raw.headers,
    body: await c.req.raw.text(),
  });
  return stub.fetch(internalReq);
});

// Large File & Image Transfers
app.post("/v1/transfers/init", async (c) => {
  const stub = getRoomStub(c);
  const internalReq = new Request("http://internal/internal/transfers/init", {
    method: "POST",
    headers: c.req.raw.headers,
    body: await c.req.raw.text(),
  });
  return stub.fetch(internalReq);
});

app.get("/v1/transfers/pending", (c) => {
  const stub = getRoomStub(c);
  const internalReq = new Request("http://internal/internal/transfers/pending", {
    method: "GET",
    headers: c.req.raw.headers,
  });
  return stub.fetch(internalReq);
});

app.post("/v1/transfers/:transferId/ack", async (c) => {
  const stub = getRoomStub(c);
  const transferId = c.req.param("transferId");
  const internalReq = new Request(`http://internal/internal/transfers/${encodeURIComponent(transferId)}/ack`, {
    method: "POST",
    headers: c.req.raw.headers,
    body: await c.req.raw.text(),
  });
  return stub.fetch(internalReq);
});

app.delete("/v1/transfers/:transferId", (c) => {
  const stub = getRoomStub(c);
  const transferId = c.req.param("transferId");
  const internalReq = new Request(`http://internal/internal/transfers/${encodeURIComponent(transferId)}`, {
    method: "DELETE",
    headers: c.req.raw.headers,
  });
  return stub.fetch(internalReq);
});

app.post("/v1/transfers/sweep", (c) => {
  const stub = getRoomStub(c);
  const internalReq = new Request("http://internal/internal/transfers/sweep", {
    method: "POST",
    headers: c.req.raw.headers,
  });
  return stub.fetch(internalReq);
});

export default app;
