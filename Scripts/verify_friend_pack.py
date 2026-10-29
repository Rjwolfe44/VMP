"""Independently read and hash every file in a completed VMP friend ZIP."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import xml.etree.ElementTree as ET
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    with zipfile.ZipFile(args.archive) as archive:
        entries = archive.infolist()
        names = [entry.filename for entry in entries]
        if len({name.casefold() for name in names}) != len(names):
            raise ValueError("Duplicate ZIP entries")
        for name in names:
            path = PurePosixPath(name)
            if path.is_absolute() or ".." in path.parts or "\\" in name or ":" in name:
                raise ValueError(f"Unsafe ZIP path: {name}")
        manifest = json.loads(archive.read("manifest.json").decode("utf-8-sig"))
        files = manifest["Files"]
        expected = {"GameData/" + item["Path"]: item for item in files}
        if len(expected) != len(files):
            raise ValueError("Duplicate manifest entries")
        actual = {name for name in names if name.startswith("GameData/") and not name.endswith("/")}
        if actual != set(expected):
            raise ValueError("ZIP payload does not exactly match its manifest")
        forbidden_roots = {"squad", "squadexpansion", "lunamultiplayer", "lunacompat", "mechjeb2"}
        total_bytes = 0
        for name, item in expected.items():
            if PurePosixPath(name).parts[1].casefold() in forbidden_roots:
                raise ValueError(f"Excluded game/mod payload: {name}")
            if name.startswith("GameData/VladMultiplayer/Data/") and name != "GameData/VladMultiplayer/Data/settings.xml":
                raise ValueError(f"Private VMP data: {name}")
            digest = hashlib.sha256()
            length = 0
            with archive.open(name) as stream:
                while chunk := stream.read(1024 * 1024):
                    digest.update(chunk)
                    length += len(chunk)
            if digest.hexdigest().upper() != item["Sha256"].upper() or length != item["Bytes"]:
                raise ValueError(f"Payload hash/length mismatch: {name}")
            total_bytes += length
        settings = ET.fromstring(archive.read("GameData/VladMultiplayer/Data/settings.xml"))
        if settings.findtext("PlayerName") != "Player":
            raise ValueError("Friend settings retain the host player name")
        for required in ("Install Friend Pack.bat", "Install-FriendPack.ps1", "READ ME FIRST.txt", "VMP-Career-Locked.ckan"):
            archive.read(required)  # Also verifies the ZIP CRC of the installer/docs.
        # The user's ApexGuidance fork retains the MechJeb2 assembly filename.
        if "GameData/ApexGuidance/Plugins/MechJeb2.dll" not in actual:
            raise ValueError("ApexGuidance plugin missing")
    with args.archive.open("rb") as stream:
        archive_hash = hashlib.file_digest(stream, "sha256").hexdigest().upper()
    sidecar_hash = args.archive.with_suffix(args.archive.suffix + ".sha256").read_text().split()[0]
    if archive_hash != sidecar_hash.upper():
        raise ValueError("ZIP sidecar checksum mismatch")
    report = {
        "passed": True,
        "archive": str(args.archive.resolve()),
        "archiveBytes": args.archive.stat().st_size,
        "archiveSha256": archive_hash,
        "verifiedPayloadFiles": len(files),
        "uncompressedPayloadBytes": total_bytes,
        "stockAndDlcExcluded": True,
        "conflictingModsExcluded": True,
        "hostPrivateVmpDataExcluded": True,
        "friendPlayerName": "Player",
        "allPayloadHashesAndZipCrcsChecked": True,
    }
    output = json.dumps(report, indent=2)
    if args.report:
        args.report.write_text(output + "\n", encoding="utf-8")
    print(output)


if __name__ == "__main__":
    main()
