# Neon Undelete: Microsoft Store submission kit

## Your app's identity (reserved 19 Nov 2026)

| | |
|---|---|
| Store ID | `9N07M96N4Z06` |
| Package/Identity/Name | `HisHandiwork.NeonUndelete` |
| Package/Identity/Publisher | `CN=9F716D34-43EE-4F0B-918B-8A025B55F943` |
| Package/Properties/PublisherDisplayName | `His Handiwork` |

Build the Store package (bump `-Version` for every update; the last part must stay 0):

```powershell
.\packaging\package.ps1 -IdentityName "HisHandiwork.NeonUndelete" -Publisher "CN=9F716D34-43EE-4F0B-918B-8A025B55F943" -PublisherDisplayName "His Handiwork" -Version 1.1.0.0
```

The name reservation lapses if the app isn't submitted within three months, so submit by about 19 Feb 2027.

Everything to paste into Partner Center, plus the submission steps in order.

---

## 1. One-time setup

1. **Create a developer account** at https://partner.microsoft.com/dashboard/registration. Choose an *Individual* account: it's free and needs ID verification. A *Company* account needs a registered business.
2. In Partner Center choose **Apps and games → New product → MSIX or PWA app**, then **reserve the name "Neon Undelete"**. If it's taken, reserve a variant and update `DisplayName` in `AppxManifest.template.xml` to match.
3. Open **Product management → Product identity** and copy three values:
   - `Package/Identity/Name` → `-IdentityName`
   - `Package/Identity/Publisher` (starts with `CN=`) → `-Publisher`
   - `Package/Properties/PublisherDisplayName` → `-PublisherDisplayName`
4. Build the Store package with your real identity:

```powershell
.\packaging\package.ps1 -IdentityName "<Identity Name>" -Publisher "CN=<...>" -PublisherDisplayName "<Publisher display name>" -Version 1.1.0.0
```

Upload `packaging\out\NeonUndelete_1.1.0.0_x64.msix`. Microsoft signs Store packages, so it doesn't need your own signature.

---

## 2. Packages page

- Upload the `.msix` above.
- **Device families:** Windows 10/11 Desktop only.
- **Restricted capabilities:** the package declares two, and the form asks you to justify each one. Paste these:

**runFullTrust**
> Neon Undelete is a classic Win32 desktop application (.NET Framework 4.8 WinForms GUI plus a console tool). It needs full trust because it reads the NTFS Master File Table and cluster data directly from the raw volume, which isn't possible from a sandboxed AppContainer.

**allowElevation**
> Reading a volume's raw sectors (\\.\C:) requires administrator rights on Windows; this is the core function of a deleted-file recovery tool. The app starts unelevated and only asks for elevation (a standard UAC prompt) when the user chooses to scan a drive. It opens the volume read-only (GENERIC_READ), never writes to the scanned drive, and refuses to save recovered files onto it. Disk image files can be scanned without elevation. Microsoft's own "Windows File Recovery" app in the Store needs administrator rights for the same reason.

---

## 3. Properties

| Field | Value |
|---|---|
| Category | Utilities & tools |
| Subcategory | File managers |
| Privacy policy URL | required, see `PRIVACY.md` (host it at any public URL) |
| Website | optional |
| Support contact | an email or URL you control |
| System requirements | Minimum: x64, Windows 10 1809+. Recommended: Windows 11 |
| Product declarations | ✔ "This product has been tested to meet accessibility guidelines" only if you have tested it, otherwise leave unticked |

---

## 4. Age ratings (IARC questionnaire)

Category **Utility / productivity**. Answer "no" to every content question: no violence, user interaction, data sharing, purchases or location. The expected result is 3+ / Everyone.

---

## 5. Store listing (English)

**Product name:** Neon Undelete

**Short description** (shown in search results):
> Bring back files you deleted by mistake. Scans NTFS, FAT32 and exFAT drives (PC disks, USB sticks, SD cards) for deleted files and folders, shows how recoverable each one is, previews photos, and restores them safely to another drive.

**Description:**
```
Deleted something you needed? Neon Undelete scans your drives, USB sticks and SD cards for files and folders that Windows has marked as deleted, and copies them back out, safely and read-only.

HOW IT WORKS
Every file has an entry in the drive's file table (the NTFS Master File Table, or the FAT / exFAT directory). Deleting a file only marks that entry as free; the contents stay on disk until something else overwrites them. Neon Undelete reads those structures directly, finds the freed entries, rebuilds where each file used to live, and checks the drive's allocation map to tell you whether its data is still intact.

FEATURES
• Fast scan: reads the whole file table in seconds, even on multi-terabyte drives
• Recovery rating for every item: excellent, partial, overwritten, or zeroed (SSD)
• Restores whole deleted folders with their original structure and timestamps
• Live preview of deleted photos, text and binary files before you recover them
• Search, filter by type or recovery chance, and sort by name, size, date or folder
• Mark many items and recover them in one go
• Read-only by design: never writes to the drive you are recovering from
• Works with NTFS, FAT32/FAT16/FAT12 and exFAT: internal drives, USB sticks, SD cards and camera cards
• Includes a terminal version and command line ("undelete") for scripting
• Neon retro terminal look

TIPS FOR THE BEST RESULTS
• Stop using the drive as soon as you notice the deletion. New files can overwrite the old data.
• Recover to a different drive (Neon Undelete enforces this).
• On SSDs with TRIM, deleted space is often wiped within moments; Neon Undelete detects this and tells you.

Scanning a drive requires administrator rights (Windows needs them for raw disk access); the app asks with a standard UAC prompt when you start a scan.

• Scans raw disk images and .vhd files too
• Built-in help (F1) explains every rating and step

NTFS-compressed and EFS-encrypted files are not supported in this version.
```

**What's new in this version:**
> 1.1: FAT32, FAT16, FAT12 and exFAT support (USB sticks, SD cards, camera cards), whole-disk image and .vhd scanning, and built-in help (F1).

**Product features** (one per line, max 20):
```
Scan NTFS, FAT32 and exFAT drives for deleted files and folders in seconds
Recovery rating for every item based on whether its space was reused
Restore whole folders with original structure and timestamps
Preview deleted photos, text and binary data before recovering
Search, filter and sort hundreds of thousands of results
Batch recovery of marked items
Read-only scanning; never writes to the source drive
Detects SSD TRIM-wiped data
Scan raw disk images and .vhd files without administrator rights
Built-in help explaining every rating, step and shortcut
Terminal UI and command line included
```

**Search terms** (max 7):
```
undelete
file recovery
recover deleted files
data recovery
NTFS
restore files
deleted photos
```

**Copyright:** © 2026 \<your name\>

---

## 6. Screenshots

At least one is required, 1366×768 or larger, PNG. Recommended set:
1. **The results list after a scan, with a photo in the preview pane.** This is the money shot; take it on your own machine as administrator.
2. The drive picker / start screen (`screenshots\01-start.png`, already included).
3. A recovery-complete dialog.
4. The terminal version (optional).

Plus app tile icon `packaging\Assets\Square150x150Logo.scale-200.png` (300×300). It's used automatically from the package.

---

## 7. Pricing and availability

Choose free or a price, markets (all is fine), and visibility (public, or "private, hidden" for a test run first).

---

## 8. Notes for certification (submission options)

```
Neon Undelete is a deleted-file recovery tool. To test:
1. Launch the app; it starts as a standard user.
2. Select any NTFS drive and press "scan". A UAC prompt appears (raw disk access needs admin); accept it.
3. The scan lists deleted files found on the drive. Select one to see details and a preview; press "recover" and pick a folder on a DIFFERENT drive.
The scanned drive is opened read-only. No network access, no data collection.
```

---

## Before you submit: local test install

Your own machine needs the test certificate trusted once:

```powershell
.\packaging\package.ps1 -Sign                       # builds + signs with a self-signed test cert
# in an administrator PowerShell, once:
Import-Certificate .\packaging\out\NeonUndelete-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
# then double-click packaging\out\NeonUndelete_1.1.0.0_x64.msix → Install
```

Check that:
- the Start menu entry and taskbar icon look right
- "restart as administrator" works from the installed app (this exercises `allowElevation`)
- a scan runs
- `undelete --help` works in a new terminal window

## If certification rejects `allowElevation`

There's a fallback: Partner Center also accepts classic **MSI/EXE installers** (Apps and games → New product → *EXE or MSI app*). The installer is hosted on your own HTTPS URL and must be signed with a code-signing certificate you buy, for example Azure Trusted Signing (about $10 a month) or a commercial certificate. That route has no restrictions on elevation.
