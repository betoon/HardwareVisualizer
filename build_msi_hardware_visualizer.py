import shutil
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path


APP_NAME = "HardwareVisualizer V1.0 Brian E. Toon 2026"
SHORTCUT_NAME = "HardwareVisualizer"
VERSION = "1.0.0"
MANUFACTURER = "Brian E. Toon"
UPGRADE_CODE = "80F7A2EC-8EB9-5E0B-B3E5-91BB0946CC6A"
WIX_NS = "http://schemas.microsoft.com/wix/2006/wi"
UUID_NAMESPACE = uuid.UUID("f6d98e90-4f07-4a19-94ce-74c2ac6d98bb")


ET.register_namespace("", WIX_NS)


def wx(tag):
    return f"{{{WIX_NS}}}{tag}"


def hidden_subprocess_kwargs():
    if sys.platform != "win32":
        return {}
    startupinfo = subprocess.STARTUPINFO()
    startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    return {"startupinfo": startupinfo, "creationflags": subprocess.CREATE_NO_WINDOW}


def run(command, cwd):
    print("Running:", " ".join(str(part) for part in command))
    result = subprocess.run(
        command,
        cwd=str(cwd),
        text=True,
        capture_output=True,
        **hidden_subprocess_kwargs(),
    )
    if result.returncode != 0:
        output = (result.stderr or result.stdout or "Command failed.").strip()
        raise RuntimeError(output)
    if result.stdout.strip():
        print(result.stdout.strip())


def safe_id(prefix, value):
    cleaned = []
    for char in str(value):
        if char.isalnum() or char == "_":
            cleaned.append(char)
        else:
            cleaned.append("_")
    text = "".join(cleaned).strip("_")
    if not text or not (text[0].isalpha() or text[0] == "_"):
        text = f"{prefix}_{text}"
    digest = uuid.uuid5(UUID_NAMESPACE, f"{prefix}:{value}").hex[:10]
    return f"{prefix}_{text[:48]}_{digest}"


def stable_guid(*parts):
    return str(uuid.uuid5(UUID_NAMESPACE, "|".join(str(part) for part in parts))).upper()


def find_wix_tools():
    wix = shutil.which("wix")
    candle = shutil.which("candle")
    light = shutil.which("light")
    if wix:
        return {"kind": "wix4", "wix": wix}
    if candle and light:
        return {"kind": "wix3", "candle": candle, "light": light}

    common_wix3_bins = [
        Path(r"C:\Program Files (x86)\WiX Toolset v3.14\bin"),
        Path(r"C:\Program Files (x86)\WiX Toolset v3.11\bin"),
        Path(r"C:\Program Files\WiX Toolset v3.14\bin"),
        Path(r"C:\Program Files\WiX Toolset v3.11\bin"),
    ]
    for folder in common_wix3_bins:
        candle_path = folder / "candle.exe"
        light_path = folder / "light.exe"
        if candle_path.exists() and light_path.exists():
            return {"kind": "wix3", "candle": str(candle_path), "light": str(light_path)}

    return None


def indent_xml(element, level=0):
    indent = "\n" + level * "  "
    if len(element):
        if not element.text or not element.text.strip():
            element.text = indent + "  "
        for child in element:
            indent_xml(child, level + 1)
        if not child.tail or not child.tail.strip():
            child.tail = indent
    if level and (not element.tail or not element.tail.strip()):
        element.tail = indent


def ensure_directory_nodes(root_folder, target_parent, dir_nodes):
    parts = []
    current = target_parent
    while current not in dir_nodes:
        parts.append(current)
        current = current.parent
    parent_node = dir_nodes[current]
    for folder in reversed(parts):
        node = ET.SubElement(
            parent_node,
            wx("Directory"),
            {"Id": safe_id("dir", folder.relative_to(root_folder)), "Name": folder.name},
        )
        dir_nodes[folder] = node
        parent_node = node


def write_wix_source(wxs_path, publish_dir, main_exe):
    wix = ET.Element(wx("Wix"))
    product = ET.SubElement(
        wix,
        wx("Product"),
        {
            "Id": "*",
            "Name": APP_NAME,
            "Language": "1033",
            "Version": VERSION,
            "Manufacturer": MANUFACTURER,
            "UpgradeCode": f"{{{UPGRADE_CODE}}}",
        },
    )
    ET.SubElement(
        product,
        wx("Package"),
        {
            "InstallerVersion": "500",
            "Compressed": "yes",
            "InstallScope": "perMachine",
            "Description": APP_NAME,
        },
    )
    ET.SubElement(product, wx("MajorUpgrade"), {"DowngradeErrorMessage": "A newer version is already installed."})
    ET.SubElement(product, wx("MediaTemplate"), {"EmbedCab": "yes"})
    ET.SubElement(product, wx("Property"), {"Id": "WIXUI_INSTALLDIR", "Value": "INSTALLFOLDER"})
    ET.SubElement(product, wx("Property"), {"Id": "INSTALLDESKTOPSHORTCUT", "Value": "1"})
    add_installer_ui(product)

    target_dir = ET.SubElement(product, wx("Directory"), {"Id": "TARGETDIR", "Name": "SourceDir"})
    program_files = ET.SubElement(target_dir, wx("Directory"), {"Id": "ProgramFilesFolder"})
    install_dir = ET.SubElement(program_files, wx("Directory"), {"Id": "INSTALLFOLDER", "Name": "HardwareVisualizer"})
    ET.SubElement(target_dir, wx("Directory"), {"Id": "DesktopFolder", "Name": "Desktop"})
    program_menu = ET.SubElement(target_dir, wx("Directory"), {"Id": "ProgramMenuFolder"})
    ET.SubElement(program_menu, wx("Directory"), {"Id": "ApplicationProgramsFolder", "Name": "HardwareVisualizer"})

    feature = ET.SubElement(product, wx("Feature"), {"Id": "DefaultFeature", "Title": APP_NAME, "Level": "1"})
    dir_nodes = {publish_dir: install_dir}

    for path in sorted(publish_dir.rglob("*")):
        if not path.is_file():
            continue
        parent = path.parent
        if parent not in dir_nodes:
            ensure_directory_nodes(publish_dir, parent, dir_nodes)

        rel = path.relative_to(publish_dir)
        component_id = safe_id("cmp", rel)
        file_id = safe_id("fil", rel)
        component = ET.SubElement(
            dir_nodes[parent],
            wx("Component"),
            {"Id": component_id, "Guid": f"{{{stable_guid('component', APP_NAME, rel)}}}"},
        )
        file_attrs = {"Id": file_id, "Source": str(path), "KeyPath": "yes"}
        if len(path.name) <= 72:
            file_attrs["Name"] = path.name
        ET.SubElement(component, wx("File"), file_attrs)
        ET.SubElement(feature, wx("ComponentRef"), {"Id": component_id})

        if path == main_exe:
            ET.SubElement(
                component,
                wx("Shortcut"),
                {
                    "Id": "StartMenuShortcut",
                    "Directory": "ApplicationProgramsFolder",
                    "Name": SHORTCUT_NAME,
                    "Description": APP_NAME,
                    "Target": f"[#{file_id}]",
                    "WorkingDirectory": "INSTALLFOLDER",
                },
            )

    desktop_shortcut = ET.SubElement(
        install_dir,
        wx("Component"),
        {"Id": "DesktopShortcutComponent", "Guid": f"{{{stable_guid('desktop-shortcut', APP_NAME)}}}"},
    )
    ET.SubElement(desktop_shortcut, wx("Condition")).text = "INSTALLDESKTOPSHORTCUT"
    ET.SubElement(
        desktop_shortcut,
        wx("Shortcut"),
        {
            "Id": "DesktopShortcut",
            "Directory": "DesktopFolder",
            "Name": SHORTCUT_NAME,
            "Description": APP_NAME,
            "Target": "[INSTALLFOLDER]HardwareVisualizer.exe",
            "WorkingDirectory": "INSTALLFOLDER",
        },
    )
    ET.SubElement(
        desktop_shortcut,
        wx("RegistryValue"),
        {
            "Root": "HKCU",
            "Key": r"Software\Brian E. Toon\HardwareVisualizer",
            "Name": "desktopShortcut",
            "Type": "integer",
            "Value": "1",
            "KeyPath": "yes",
        },
    )
    ET.SubElement(feature, wx("ComponentRef"), {"Id": "DesktopShortcutComponent"})

    cleanup = ET.SubElement(
        install_dir,
        wx("Component"),
        {"Id": "ApplicationProgramsFolderCleanup", "Guid": f"{{{stable_guid('menu-cleanup', APP_NAME)}}}"},
    )
    ET.SubElement(cleanup, wx("RemoveFolder"), {"Id": "ApplicationProgramsFolder", "Directory": "ApplicationProgramsFolder", "On": "uninstall"})
    ET.SubElement(
        cleanup,
        wx("RegistryValue"),
        {
            "Root": "HKCU",
            "Key": r"Software\Brian E. Toon\HardwareVisualizer",
            "Name": "installed",
            "Type": "integer",
            "Value": "1",
            "KeyPath": "yes",
        },
    )
    ET.SubElement(feature, wx("ComponentRef"), {"Id": "ApplicationProgramsFolderCleanup"})

    indent_xml(wix)
    ET.ElementTree(wix).write(wxs_path, encoding="utf-8", xml_declaration=True)


def add_installer_ui(product):
    ui = ET.SubElement(product, wx("UI"))
    ET.SubElement(ui, wx("UIRef"), {"Id": "WixUI_InstallDir"})
    ET.SubElement(
        ui,
        wx("Publish"),
        {
            "Dialog": "InstallDirDlg",
            "Control": "Next",
            "Event": "NewDialog",
            "Value": "ShortcutOptionsDlg",
            "Order": "5",
        },
    ).text = "WIXUI_DONTVALIDATEPATH OR WIXUI_INSTALLDIR_VALID"
    ET.SubElement(
        ui,
        wx("Publish"),
        {
            "Dialog": "VerifyReadyDlg",
            "Control": "Back",
            "Event": "NewDialog",
            "Value": "ShortcutOptionsDlg",
            "Order": "1",
        },
    ).text = "NOT Installed"

    dialog = ET.SubElement(ui, wx("Dialog"), {"Id": "ShortcutOptionsDlg", "Width": "370", "Height": "270", "Title": "[ProductName] Setup"})
    ET.SubElement(dialog, wx("Control"), {"Id": "BannerBitmap", "Type": "Bitmap", "X": "0", "Y": "0", "Width": "370", "Height": "44", "TabSkip": "no", "Text": "WixUI_Bmp_Banner"})
    ET.SubElement(dialog, wx("Control"), {"Id": "Title", "Type": "Text", "X": "15", "Y": "6", "Width": "340", "Height": "15", "Transparent": "yes", "NoPrefix": "yes", "Text": "{\\WixUI_Font_Title}Installation Options"})
    ET.SubElement(dialog, wx("Control"), {"Id": "Description", "Type": "Text", "X": "25", "Y": "48", "Width": "320", "Height": "30", "NoPrefix": "yes", "Text": "Choose whether Setup should add a Desktop shortcut for HardwareVisualizer."})
    ET.SubElement(dialog, wx("Control"), {"Id": "CreateDesktopShortcut", "Type": "CheckBox", "X": "25", "Y": "90", "Width": "320", "Height": "18", "Property": "INSTALLDESKTOPSHORTCUT", "CheckBoxValue": "1", "Text": "Create a Desktop shortcut"})
    ET.SubElement(dialog, wx("Control"), {"Id": "BannerLine", "Type": "Line", "X": "0", "Y": "44", "Width": "370", "Height": "0"})
    ET.SubElement(dialog, wx("Control"), {"Id": "BottomLine", "Type": "Line", "X": "0", "Y": "234", "Width": "370", "Height": "0"})
    ET.SubElement(dialog, wx("Control"), {"Id": "Back", "Type": "PushButton", "X": "180", "Y": "243", "Width": "56", "Height": "17", "Text": "!(loc.WixUIBack)"})
    ET.SubElement(dialog, wx("Control"), {"Id": "Next", "Type": "PushButton", "X": "236", "Y": "243", "Width": "56", "Height": "17", "Default": "yes", "Text": "!(loc.WixUINext)"})
    ET.SubElement(dialog, wx("Control"), {"Id": "Cancel", "Type": "PushButton", "X": "304", "Y": "243", "Width": "56", "Height": "17", "Cancel": "yes", "Text": "!(loc.WixUICancel)"})

    ET.SubElement(
        ui,
        wx("Publish"),
        {"Dialog": "ShortcutOptionsDlg", "Control": "Back", "Event": "NewDialog", "Value": "InstallDirDlg"},
    ).text = "1"
    ET.SubElement(
        ui,
        wx("Publish"),
        {"Dialog": "ShortcutOptionsDlg", "Control": "Next", "Event": "NewDialog", "Value": "VerifyReadyDlg"},
    ).text = "1"
    ET.SubElement(
        ui,
        wx("Publish"),
        {"Dialog": "ShortcutOptionsDlg", "Control": "Cancel", "Event": "SpawnDialog", "Value": "CancelDlg"},
    ).text = "1"


def compile_wix(wix_tools, wxs_path, work_dir, msi_path):
    if wix_tools["kind"] == "wix4":
        command = [wix_tools["wix"], "build", str(wxs_path), "-ext", "WixToolset.UI.wixext", "-out", str(msi_path)]
        try:
            run(command, work_dir)
        except RuntimeError as exc:
            if "ICE" not in str(exc) and "validation" not in str(exc).lower():
                raise
            print("WiX validation could not run in this Windows session. Retrying without MSI validation.")
            run(command + ["-sval"], work_dir)
        return

    wixobj = work_dir / "Product.wixobj"
    run([wix_tools["candle"], str(wxs_path), "-out", str(wixobj)], work_dir)
    command = [wix_tools["light"], "-ext", "WixUIExtension", str(wixobj), "-out", str(msi_path)]
    try:
        run(command, work_dir)
    except RuntimeError as exc:
        if "ICE" not in str(exc) and "validation" not in str(exc).lower():
            raise
        print("WiX validation could not run in this Windows session. Retrying without MSI validation.")
        run(command + ["-sval"], work_dir)


def copy_notice_files(project_dir, publish_dir):
    for name in ("README.txt", "THIRD_PARTY_NOTICES.txt"):
        source = project_dir / name
        if source.exists():
            shutil.copy2(source, publish_dir / name)

    libre_dir = project_dir.parent / "github" / "LibreHardwareMonitor-master"
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.txt"):
        source = libre_dir / name
        if source.exists():
            target_name = f"LibreHardwareMonitor-{name}"
            shutil.copy2(source, publish_dir / target_name)


def main():
    project_dir = Path(__file__).resolve().parent
    publish_dir = project_dir / "installer" / "publish"
    work_dir = project_dir / "installer" / "wix"
    output_dir = project_dir / "installer" / "output"
    main_exe = publish_dir / "HardwareVisualizer.exe"
    msi_path = output_dir / "HardwareVisualizer-1.0.0.msi"

    if publish_dir.exists():
        shutil.rmtree(publish_dir)
    work_dir.mkdir(parents=True, exist_ok=True)
    output_dir.mkdir(parents=True, exist_ok=True)

    run(
        [
            "dotnet",
            "publish",
            "HardwareVisualizer.csproj",
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-p:Platform=x64",
            "-p:PublishSingleFile=false",
            "-o",
            str(publish_dir),
        ],
        project_dir,
    )

    if not main_exe.exists():
        raise RuntimeError("The published HardwareVisualizer.exe was not found.")

    copy_notice_files(project_dir, publish_dir)
    wxs_path = work_dir / "Product.wxs"
    write_wix_source(wxs_path, publish_dir, main_exe)

    wix_tools = find_wix_tools()
    if not wix_tools:
        print()
        print("Publish folder created, but WiX Toolset was not found on PATH.")
        print(f"WiX source was written to: {wxs_path}")
        print("Install WiX Toolset, then run build_msi.bat again to create the MSI.")
        return 0

    compile_wix(wix_tools, wxs_path, work_dir, msi_path)
    print()
    print(f"MSI created: {msi_path}")
    print("The installer shows the HardwareVisualizer V1.0 Brian E. Toon 2026 name,")
    print("asks for the installation directory, and creates Desktop and Start Menu shortcuts.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        raise SystemExit(1)
