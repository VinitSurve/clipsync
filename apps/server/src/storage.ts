import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { S3Client, PutObjectCommand, GetObjectCommand } from "@aws-sdk/client-s3";
import { getSignedUrl } from "@aws-sdk/s3-request-presigner";
import type { UploadUrlResponse, DownloadUrlResponse } from "@clipsync/protocol";

export interface StorageService {
  generateUploadUrl(groupId: string, fileName: string, mimeType: string, sizeBytes: number, sha256: string): Promise<UploadUrlResponse>;
  generateDownloadUrl(storageRef: string): Promise<DownloadUrlResponse>;
}

export class S3StorageService implements StorageService {
  private s3: S3Client;
  private bucket: string;

  constructor(config: { endpoint?: string; region?: string; bucket: string; accessKeyId: string; secretAccessKey: string }) {
    this.bucket = config.bucket;
    this.s3 = new S3Client({
      region: config.region ?? "us-east-1",
      endpoint: config.endpoint,
      forcePathStyle: Boolean(config.endpoint), // Needed for MinIO / local S3
      credentials: {
        accessKeyId: config.accessKeyId,
        secretAccessKey: config.secretAccessKey,
      },
    });
  }

  async generateUploadUrl(groupId: string, fileName: string, mimeType: string, sizeBytes: number, sha256: string): Promise<UploadUrlResponse> {
    const key = `blobs/${groupId}/${Date.now()}-${crypto.randomBytes(8).toString("hex")}-${path.basename(fileName)}`;
    const command = new PutObjectCommand({
      Bucket: this.bucket,
      Key: key,
      ContentType: mimeType,
      ContentLength: sizeBytes,
      Metadata: {
        sha256,
      },
    });

    const uploadUrl = await getSignedUrl(this.s3, command, { expiresIn: 3600 });
    return {
      uploadUrl,
      storageRef: key,
      expiresInSeconds: 3600,
    };
  }

  async generateDownloadUrl(storageRef: string): Promise<DownloadUrlResponse> {
    const command = new GetObjectCommand({
      Bucket: this.bucket,
      Key: storageRef,
    });
    const downloadUrl = await getSignedUrl(this.s3, command, { expiresIn: 3600 });
    return {
      downloadUrl,
      storageRef,
      expiresInSeconds: 3600,
    };
  }
}

/**
 * Local file storage fallback when S3 / MinIO is not configured.
 * Operates via local API endpoints /v1/storage/local-upload and /v1/storage/local-download.
 */
export class LocalDiskStorageService implements StorageService {
  private baseDir: string;
  private serverOrigin: string;

  constructor(serverOrigin: string, storageDir?: string) {
    this.serverOrigin = serverOrigin.replace(/\/$/, "");
    this.baseDir = storageDir ?? path.resolve(process.cwd(), ".clipsync-blobs");
    if (!fs.existsSync(this.baseDir)) {
      fs.mkdirSync(this.baseDir, { recursive: true });
    }
  }

  async generateUploadUrl(groupId: string, fileName: string, _mimeType: string, _sizeBytes: number, sha256: string): Promise<UploadUrlResponse> {
    const key = `${groupId}-${Date.now()}-${crypto.randomBytes(8).toString("hex")}-${path.basename(fileName)}`;
    const uploadUrl = `${this.serverOrigin}/v1/storage/local-upload?key=${encodeURIComponent(key)}&sha256=${encodeURIComponent(sha256)}`;
    return {
      uploadUrl,
      storageRef: key,
      expiresInSeconds: 3600,
    };
  }

  async generateDownloadUrl(storageRef: string): Promise<DownloadUrlResponse> {
    const downloadUrl = `${this.serverOrigin}/v1/storage/local-download?key=${encodeURIComponent(storageRef)}`;
    return {
      downloadUrl,
      storageRef,
      expiresInSeconds: 3600,
    };
  }

  saveBlob(key: string, data: Buffer): void {
    const safeKey = path.basename(key);
    fs.writeFileSync(path.join(this.baseDir, safeKey), data);
  }

  getBlob(key: string): Buffer | null {
    const safeKey = path.basename(key);
    const target = path.join(this.baseDir, safeKey);
    if (!fs.existsSync(target)) return null;
    return fs.readFileSync(target);
  }
}
