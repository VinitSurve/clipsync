# ClipSync — Production Deployment Guide

This guide describes how to deploy the ClipSync cloud backend in production with high availability, persistent PostgreSQL storage, and S3-compatible object storage.

---

## 1. Quick Deployment with Docker Compose

The `infrastructure/docker-compose.yml` file sets up a production-ready stack:

- **PostgreSQL 16**: Persistent database for devices, pairing sessions, and metadata.
- **MinIO**: S3-compatible object storage for encrypted files and images.
- **ClipSync Server**: Node.js + TypeScript service with WebSocket relay.

```bash
cd infrastructure
docker compose up -d
```

Check logs:
```bash
docker compose logs -f server
```

Access the MinIO web console at `http://localhost:9001` (user: `clipsync_minio_admin`, password: `clipsync_minio_secret_key`).

---

## 2. Cloud Platform Deployment (Fly.io, Railway, Render)

### PostgreSQL Database
Create a managed PostgreSQL database (Postgres 15+) and set the connection string:
```bash
DATABASE_URL=postgres://user:password@host:5432/clipsync
```
Run `infrastructure/schema.sql` to initialize tables.

### S3 Object Storage
Create an AWS S3 bucket or Cloudflare R2 bucket:
- Enable CORS for your client origins.
- Configure IAM credentials with `s3:PutObject` and `s3:GetObject` permissions.
- Set environment variables:
  ```bash
  S3_BUCKET=my-clipsync-blobs
  AWS_REGION=us-east-1
  AWS_ACCESS_KEY_ID=...
  AWS_SECRET_ACCESS_KEY=...
  ```

### Server Deployment
Deploy the container from `apps/server/Dockerfile` or run with Node 20+:
```bash
pnpm install --prod
pnpm run build
pnpm start:server
```

---

## 3. Reverse Proxy & TLS Configuration (Caddy / Nginx)

ClipSync requires TLS (`https://` and `wss://`) in production.

### Caddy Example (`Caddyfile`)
```caddy
sync.yourdomain.com {
    reverse_proxy localhost:8787
}
```

### Nginx Example
```nginx
server {
    server_name sync.yourdomain.com;

    location / {
        proxy_pass http://localhost:8787;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_read_timeout 86400s;
        proxy_send_timeout 86400s;
    }
}
```

---

## 4. Health Check & Monitoring

- Health check: `GET /health` returns `200 OK` with JSON metadata.
- Monitor active WebSocket connections via process metrics.
- Keep in mind that clipboard payloads and master keys are never logged by the server.
