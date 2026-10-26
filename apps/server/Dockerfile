FROM node:20-alpine AS builder

WORKDIR /app
RUN npm install -g pnpm

COPY pnpm-workspace.yaml package.json pnpm-lock.yaml* .npmrc* ./
COPY packages ./packages
COPY apps/server ./apps/server

RUN pnpm install --frozen-lockfile || pnpm install
RUN pnpm -r run build

FROM node:20-alpine AS runner

WORKDIR /app
ENV NODE_ENV=production

COPY --from=builder /app/package.json ./
COPY --from=builder /app/node_modules ./node_modules
COPY --from=builder /app/packages ./packages
COPY --from=builder /app/apps/server ./apps/server

EXPOSE 8787
CMD ["node", "apps/server/dist/index.js"]
