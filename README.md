
# Serverus Drive (FileServer)

Serverus is a self-hosted cloud system where a user connects to their home network using a secure VPN (Tailscale), then uploads and downloads files through a custom-built C# server. It replaces public cloud services by keeping all data stored locally while still being accessible remotely.

## Features

- Remote access using **Tailscale** (no WireGuard/DuckDNS required)
- Download files to the browser’s default save dialog
- Star/unstar files (persisted)
- Soft delete to Trash (persisted), restore, and permanent delete from Trash
- Optional encryption endpoint for uploaded files (AES-CBC) and `ivBase64` response for later decryption

## Tech Stack

- Backend: **ASP.NET Core Minimal API** (C#)
- Frontend: **Tailwind CSS** via CDN (single-page UI)
- Storage:
  - Uploaded files: `FileServer1/storage/`
  - Trash files: `FileServer1/storage/.trash/`
  - Metadata (uploaded time, starred, trashed flags): `FileServer1/storage/.meta.json`

## How to run (Windows)

1. Start the server (from `FileServer/FileServer1`):

   ```powershell
   dotnet run --urls "http://0.0.0.0:5287"
   ```

2. Open the UI from another device on your Tailscale tailnet:

   - If you enabled **Tailscale MagicDNS**, use:
     - `http://<device-name>.<tailnet-name>.ts.net:5287/`
   - Otherwise use the Windows machine’s Tailscale IP:
     - `http://<tailscale-ip>:5287/`

## Tailscale setup (quick)

1. Install **Tailscale** on your Windows PC (FileServer machine) and on your phone.
2. Log into both with the same account / tailnet.
3. (Recommended) Enable **MagicDNS** in the Tailscale admin console.
4. Ensure the server port (`5287`) is reachable over Tailscale (usually automatic; Windows firewall rule for TCP 5287 may be required).

## API Reference

### Upload

- `POST /api/upload`
  - `multipart/form-data`
  - form field: `file` (`IFormFile`)
  - returns: `201 Created` with `fileName`

- `POST /api/upload-multiple`
  - `multipart/form-data`
  - form fields: `files` (multiple)
  - returns: `[{ "fileName": "..." }, ...]`

### Files list

- `GET /api/files?includeTrashed=true`
  - returns array of file objects with metadata:
    - `name`, `size`, `modified` (UTC ISO-8601)
    - `uploaded` (UTC ISO-8601)
    - `starred` (boolean)
    - `trashed` (boolean)
    - `trashedAt` (UTC ISO-8601 or null)

### Preview

- `GET /api/preview/{fileName}`
  - serves file inline with correct MIME type when possible (used by the UI)

### Download

- `GET /api/download/{fileName}`
  - returns the file as `application/octet-stream`

### Star / Unstar

- `POST /api/star/{fileName}`
  - body (JSON): `{ "starred": true|false }`

### Trash / Restore

- `POST /api/trash/{fileName}`
  - moves the file into `storage/.trash/` (soft delete) and marks metadata as trashed

- `POST /api/restore/{fileName}`
  - moves the file back into `storage/` (restores from trash)

- `DELETE /api/trash/{fileName}`
  - permanently deletes a file from `storage/.trash/`

### Encryption (optional)

- `POST /api/encrypt`
  - `multipart/form-data`
  - fields: `file` and `key` (string)
  - stores encrypted output as `<original>.enc`
  - returns:
    - `encryptedFileName`
    - `ivBase64` (required to decrypt)
    - `algorithm` (currently `AES-CBC-PKCS7`)

## Notes / Current Limitations

- The UI and endpoints are designed for a school/demo environment.
- Encryption uses a simple `SHA256(key)` to produce the AES key (fine for demos; not recommended for production-grade security without a proper KDF).
- Some API endpoints disable anti-forgery checks to simplify uploading from a static page.

