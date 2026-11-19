using System.Collections.Generic;

namespace Undelete
{
    /// <summary>One piece of help text. Kind: 'h' heading, 'p' paragraph, 'l' bullet, 'k' key + description, 'w' warning.</summary>
    sealed class HelpBlock
    {
        public char Kind;
        public string A, B;
        public HelpBlock(char kind, string a, string b = null) { Kind = kind; A = a; B = b; }
    }

    sealed class HelpTopic
    {
        public string Title;
        public List<HelpBlock> Blocks = new List<HelpBlock>();
        public HelpTopic(string title) { Title = title; }
        public HelpTopic H(string s) { Blocks.Add(new HelpBlock('h', s)); return this; }
        public HelpTopic P(string s) { Blocks.Add(new HelpBlock('p', s)); return this; }
        public HelpTopic L(string s) { Blocks.Add(new HelpBlock('l', s)); return this; }
        public HelpTopic K(string key, string what) { Blocks.Add(new HelpBlock('k', key, what)); return this; }
        public HelpTopic W(string s) { Blocks.Add(new HelpBlock('w', s)); return this; }
    }

    /// <summary>Help shared by the windowed app and the terminal UI.</summary>
    static class HelpContent
    {
        public static List<HelpTopic> Topics(bool gui)
        {
            var t = new List<HelpTopic>();

            t.Add(new HelpTopic("getting started")
                .P("Neon Undelete finds files and folders that Windows has marked as deleted, and copies them back out to a different drive. It never writes to the drive it is scanning.")
                .H("recover a file in four steps")
                .L(gui ? "Pick the drive the file was deleted from in the volumes panel, then press scan."
                       : "Pick the drive the file was deleted from and press Enter to scan.")
                .L(gui ? "Find the file: type part of its name in search, or sort by modified to see recent deletions first."
                       : "Find the file: press / and type part of its name, or press S to sort by modified date.")
                .L(gui ? "Mark it (click the ◇, or press Space). Mark a deleted folder to bring back everything inside it."
                       : "Mark it with Space. Mark a deleted folder to bring back everything inside it.")
                .L(gui ? "Choose a destination on another drive and press recover."
                       : "Press R, type a destination folder on another drive, and press Enter.")
                .W("Stop using the drive as soon as you notice the deletion. Every new file written to it can overwrite the data you want back.")
                .H("administrator rights")
                .P("Windows only lets administrators read a drive directly. " +
                   (gui ? "Use \"restart as administrator\" in the top-right corner, or tick \"always start as administrator\" when installing."
                        : "Press E on the drive screen to relaunch as administrator.") +
                   " Disk image files can be scanned without it."));

            t.Add(new HelpTopic("recovery chances")
                .P("Each deleted item gets a rating that says how likely its contents are to come back intact. It is worked out from the drive's allocation map: once a cluster is given to another file, the old data in it is gone.")
                .K("■■■■■", "excellent: none of its space has been reused. It should recover perfectly.")
                .K("■■□□□", "partial: some clusters now belong to other files. Expect a damaged file; photos may be half grey, documents may not open.")
                .K("□□□□□", "overwritten: all of its space has been reused. The name is still listed but the contents are gone.")
                .K("zeros", "the space reads back as zeros. SSDs wipe freed space (TRIM) within moments, so deleted files on SSDs are usually unrecoverable.")
                .K("n/a", "NTFS-compressed or EFS-encrypted. Not supported in this version.")
                .K("folder", "a deleted folder. Recovering it restores the deleted items found inside it.")
                .P("On FAT drives the rating assumes the file was stored in one piece (see file systems). Check the preview before relying on it."));

            t.Add(new HelpTopic("file systems")
                .P("Supported: NTFS (Windows drives), FAT32 / FAT16 / FAT12 (older USB sticks, SD cards, cameras) and exFAT (most modern USB drives and SD cards).")
                .H("ntfs")
                .L("Best results. Deleted files keep their full name, folder, size, dates and the exact location of every fragment.")
                .L("Very small files (under ~700 bytes) live inside the file table itself and almost always recover perfectly.")
                .L("Folders that were themselves deleted are rebuilt; if a parent folder's record was reused the item is shown under ‹lost›.")
                .H("exfat")
                .L("Deleted files keep their full name, size, dates and first cluster.")
                .L("Files written in one piece are flagged as contiguous, so their location is exact.")
                .L("Fragmented files follow the remains of their cluster chain; where it was cleared the rest is assumed to follow on directly.")
                .H("fat32 / fat16 / fat12")
                .L("Deleting a file overwrites the first letter of its short (8.3) name. Long names survive and are reattached automatically, so most files show their real name. Files with only a short name start with \"_\".")
                .L("FAT erases the file's cluster chain, so the tool assumes the file was stored in one continuous block. That is true for most photos and videos written straight from a camera, but fragmented files will come back partly wrong. Check the preview.")
                .L("Deleted folders are recovered as long as their first cluster still holds the folder listing."));

            t.Add(new HelpTopic("recovering files")
                .L("Recover marked items, or just the selected one if nothing is marked.")
                .L("The destination must be on a different drive. Writing to the drive you are recovering from could overwrite the very data you want back, so the tool refuses.")
                .L("Files are saved under <destination>\\Undeleted_<drive>_<date-time>\\, keeping their original folder structure.")
                .L("Items whose parent folder could not be identified go into _lost_parent_folder\\.")
                .L("Original created and modified times are restored. If two items would get the same name, \" (2)\" is added.")
                .W("A recovered file can still be damaged if part of its space was reused. Open it to check before deleting anything else."));

            var keys = new HelpTopic("keyboard");
            if (gui)
            {
                keys.H("anywhere")
                    .K("F1", "open this help")
                    .K("Ctrl+F", "jump to search")
                    .K("F5", "scan the selected drive")
                    .K("Esc", "clear search (while typing in it)")
                    .H("results list")
                    .K("↑ ↓ PgUp PgDn", "move")
                    .K("Home / End", "first / last item")
                    .K("Space", "mark or unmark, then move down")
                    .K("Ctrl+click", "mark or unmark")
                    .K("Shift+click", "mark a range")
                    .K("Ctrl+A", "mark all shown (again to unmark)")
                    .K("Enter / double-click", "recover the selected item")
                    .K("click a header", "sort by that column (again to reverse)");
            }
            else
            {
                keys.H("drive screen")
                    .K("↑ ↓", "choose a drive")
                    .K("Enter", "scan")
                    .K("E", "relaunch as administrator")
                    .K("F5", "refresh the drive list")
                    .K("F1 / ?", "help")
                    .K("Q / Esc", "quit")
                    .H("results")
                    .K("↑ ↓ PgUp PgDn Home End", "move")
                    .K("Space", "mark or unmark, then move down")
                    .K("A / X", "mark all shown / clear marks")
                    .K("/", "search name or folder (Enter keeps, Esc clears)")
                    .K("T", "type filter: all, files, folders")
                    .K("C", "chance filter: any, recoverable, excellent")
                    .K("S / I", "change sort / reverse it")
                    .K("Z", "show or hide empty files")
                    .K("R / Enter", "recover")
                    .K("F1 / ?", "help")
                    .K("Esc", "back to drives")
                    .K("Q", "quit");
            }
            t.Add(keys);

            t.Add(new HelpTopic("tips & troubleshooting")
                .H("nothing found, or my file is missing")
                .L("Wait about 20 seconds after deleting before you scan. Windows writes deletions to disk lazily.")
                .L("Files in the Recycle Bin are not deleted yet. Restore them from the Recycle Bin.")
                .L("Set the chance filter to \"any\" and turn off \"hide empty\". Overwritten and empty entries are hidden by default.")
                .L("On a busy drive, the entry of a deleted file is soon reused by new files, and then it can't be found at all.")
                .H("errors")
                .L("\"Access denied\": run as administrator.")
                .L("\"Unrecognised file system\": the drive isn't NTFS, FAT or exFAT (for example ReFS, or a BitLocker-locked drive). Unlock BitLocker drives in Explorer first.")
                .L("Some drives refuse direct reads (Win32 error 50). The tool then reads the drive through its physical disk automatically.")
                .H("disk images")
                .L("You can scan raw images (.img, .dd, .raw, .bin) and fixed-size .vhd files, either of a single volume or a whole disk; the first supported partition is used.")
                .L("Imaging a failing drive first, then scanning the image, is the safest way to recover from it."));

            t.Add(new HelpTopic("command line")
                .P("The terminal version, Undelete.exe, also works from scripts:")
                .K("undelete", "terminal UI")
                .K("undelete E:", "terminal UI, scan E: straight away")
                .K("undelete --list E: out.csv", "scan and write every deleted entry to a CSV file")
                .K("undelete --recover E: D:\\Out text", "recover every entry whose path contains text (* for all)")
                .K("undelete --help", "all options")
                .P("If you ticked \"add 'undelete' to PATH\" when installing, these work from any terminal."));

            t.Add(new HelpTopic("about")
                .P("Neon Undelete " + AppInfo.Version + ". A read-only deleted-file recovery tool for NTFS, FAT and exFAT drives.")
                .L("Reads drives read-only; never writes to the source.")
                .L("Makes no network connections and collects no data.")
                .L("Not supported yet: NTFS-compressed and EFS-encrypted files, ReFS, and carving files whose directory entries have been overwritten."));

            return t;
        }
    }

    static class AppInfo
    {
        public const string Version = "1.1.0";
    }
}
