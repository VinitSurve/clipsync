import http from "node:http";
import express from "express";
import cors from "cors";
import dotenv from "dotenv";
import { createStorage } from "./db.js";
import { S3StorageService, LocalDiskStorageService, type StorageService } from "./storage.js";
import { WebSocketRelayManager } from "./websocket.js";
import { createRouter } from "./routes.js";

dotenv.config();

const port = Number(process.env.PORT ?? 8787);
const host = process.env.HOST ?? "0.0.0.0";
const serverOrigin = process.env.SERVER_ORIGIN ?? `http://${host === "0.0.0.0" ? "localhost" : host}:${port}`;

async function main() {
  const app = express();
  const server = http.createServer(app);

  app.use(cors());
  app.use(express.json({ limit: "5mb" }));

  // Initialize storage
  const storage = await createStorage(process.env.DATABASE_URL);

  // Initialize object storage (S3 / MinIO or local fallback)
  let storageService: StorageService;
  if (process.env.S3_BUCKET && process.env.AWS_ACCESS_KEY_ID && process.env.AWS_SECRET_ACCESS_KEY) {
    storageService = new S3StorageService({
      endpoint: process.env.S3_ENDPOINT,
      region: process.env.AWS_REGION ?? "us-east-1",
      bucket: process.env.S3_BUCKET,
      accessKeyId: process.env.AWS_ACCESS_KEY_ID,
      secretAccessKey: process.env.AWS_SECRET_ACCESS_KEY,
    });
    console.log(`[ClipSync] Initialized S3 object storage (Bucket: ${process.env.S3_BUCKET})`);
  } else {
    storageService = new LocalDiskStorageService(serverOrigin);
    console.log(`[ClipSync] Initialized local disk blob storage`);
  }

  // Initialize WebSocket relay
  const relayManager = new WebSocketRelayManager(server, storage);

  // Mount routes
  const router = createRouter(storage, storageService, relayManager, serverOrigin);
  app.use(router);

  server.listen(port, host, () => {
    console.log(`=======================================================`);
    console.log(`🚀 ClipSync Universal Server running at http://${host}:${port}`);
    console.log(`📡 WebSocket endpoint at ws://${host}:${port}/v1/events`);
    console.log(`=======================================================`);
  });

  const shutdown = async () => {
    console.log("[ClipSync] Shutting down gracefully...");
    await storage.close();
    server.close(() => process.exit(0));
  };

  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

main().catch((err) => {
  console.error("Fatal startup error:", err);
  process.exit(1);
});
