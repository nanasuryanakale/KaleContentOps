# Authentication & Access Foundation

ASP.NET Core Identity + cookie authentication. Permission-based authorization:
roles carry `permission` claims, dan kode mengotorisasi berdasarkan permission
(tidak pernah hard-code nama role).

## Cara menjalankan pertama kali (Development)

1. Pastikan `ConnectionStrings:DefaultConnection` tersedia
   (appsettings.Development.json / user secrets).
2. Migration Identity (`AddIdentitySchema`) dijalankan otomatis saat startup
   (`Database.Migrate()`), atau manual: `dotnet dotnet-ef database update`.
3. Buat administrator pertama via **user secrets** (jangan pernah lewat source code):

   ```bash
   cd KaleContentOps
   dotnet user-secrets set "InitialAdmin:Username"  "admin"
   dotnet user-secrets set "InitialAdmin:Email"     "admin@kale.co.id"
   dotnet user-secrets set "InitialAdmin:DisplayName" "Administrator"
   dotnet user-secrets set "InitialAdmin:Password"  "Password-Panu!"   # min 10 char, upper+lower+digit+symbol
   ```

   atau via environment variable di host (production):

   ```
   InitialAdmin__Username=admin
   InitialAdmin__Email=admin@kale.co.id
   InitialAdmin__Password=...
   ```

4. Jalankan aplikasi. Seeder idempoten akan membuat role Administrator/Manager/Viewer,
   melampirkan permission claims, dan membuat admin pertama **hanya jika belum ada admin**.
5. Login di `/Account/Login`.

## Keamanan password

- Password admin **tidak pernah** disimpan di migration atau source code.
- Nilai di appsettings.json sengaja kosong; production wajib env var
  `InitialAdmin__Password` di host.
- Password di-hash oleh ASP.NET Core Identity `UserManager` (PBKDF2).
- `.gitignore` tidak berubah; user secrets tersimpan di luar repository
  (`%APPDATA%\Microsoft\UserSecrets\{UserSecretsId}`).

## Permission yang tersedia (MVP)

| Permission           | Administrator | Manager | Viewer |
|----------------------|:---:|:---:|:---:|
| Target.View          | ✅ | ✅ | ✅ |
| Target.Edit          | ✅ | ✅ | ❌ |
| Target.History.View  | ✅ | ✅ | ✅ |

Administrator selalu mendapat semua permission yang dikenal sistem.
Permission baru: tambahkan konstanta di `Security/AuthConstants.Permissions`,
lalu map ke role pada `AuthConstants.DefaultRolePermissions` (tanpa ubah skema DB).

## Opsi konfigurasi

| Key | Default | Arti |
|-----|---------|------|
| `Identity:SeedOnStartup` | `true` | Jalankan seeder saat startup (test host set `false`) |
| `InitialAdmin:Enabled` | `true` | Matikan seeder sepenuhnya (multi-instance rollout) |
| `InitialAdmin:Username/Email/DisplayName/Password` | kosong | Kredensial admin pertama |

## Catatan routing 401/403

Request JSON (`Content-Type: application/json`) atau non-browser (tanpa
`Accept: text/html`) menerima status 401/403 polos, bukan redirect HTML -
sehingga auto-save `targets.js` bisa menangani error dengan benar.
