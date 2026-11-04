"""Exercise actual startup dialogs in isolated copies; never modifies the installed config/database."""
import argparse
import ctypes as C
from ctypes import wintypes as W
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

u = C.WinDLL('user32', use_last_error=True)
k = C.WinDLL('kernel32', use_last_error=True)
callback = C.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)
u.EnumWindows.argtypes = [callback, W.LPARAM]
u.EnumChildWindows.argtypes = [W.HWND, callback, W.LPARAM]
u.GetWindowThreadProcessId.argtypes = [W.HWND, C.POINTER(W.DWORD)]
u.GetWindowTextLengthW.argtypes = [W.HWND]
u.GetWindowTextW.argtypes = [W.HWND, W.LPWSTR, C.c_int]
u.PostMessageW.argtypes = [W.HWND, W.UINT, W.WPARAM, W.LPARAM]
u.GetDlgItem.argtypes = [W.HWND, C.c_int]
u.GetDlgItem.restype = W.HWND
u.GetDC.argtypes = [W.HWND]
u.GetDC.restype = W.HDC
u.ReleaseDC.argtypes = [W.HWND, W.HDC]
g = C.WinDLL('gdi32')
g.GetPixel.argtypes = [W.HDC, C.c_int, C.c_int]
g.GetPixel.restype = W.DWORD
k.CreateToolhelp32Snapshot.argtypes = [W.DWORD, W.DWORD]
k.CreateToolhelp32Snapshot.restype = W.HANDLE
k.CloseHandle.argtypes = [W.HANDLE]
k.OpenProcess.argtypes = [W.DWORD, W.BOOL, W.DWORD]
k.OpenProcess.restype = W.HANDLE
k.QueryFullProcessImageNameW.argtypes = [W.HANDLE, W.DWORD, W.LPWSTR, C.POINTER(W.DWORD)]
k.TerminateProcess.argtypes = [W.HANDLE, W.UINT]
k.GetExitCodeProcess.argtypes = [W.HANDLE, C.POINTER(W.DWORD)]


class ProcessEntry(C.Structure):
    _fields_ = [('size', W.DWORD), ('usage', W.DWORD), ('pid', W.DWORD), ('heap', C.c_size_t),
                ('module', W.DWORD), ('threads', W.DWORD), ('parent', W.DWORD),
                ('priority', W.LONG), ('flags', W.DWORD), ('exe', W.WCHAR * 260)]


def descendants(root):
    snapshot = k.CreateToolhelp32Snapshot(2, 0)
    entry = ProcessEntry()
    entry.size = C.sizeof(entry)
    pairs = []
    k.Process32FirstW.argtypes = k.Process32NextW.argtypes = [W.HANDLE, C.POINTER(ProcessEntry)]
    try:
        more = k.Process32FirstW(snapshot, C.byref(entry))
        while more:
            pairs.append((entry.pid, entry.parent))
            more = k.Process32NextW(snapshot, C.byref(entry))
    finally:
        k.CloseHandle(snapshot)
    found = {root}
    while True:
        expanded = found | {pid for pid, parent in pairs if parent in found}
        if expanded == found:
            return found
        found = expanded


def text(hwnd):
    buffer = C.create_unicode_buffer(u.GetWindowTextLengthW(hwnd) + 1)
    u.GetWindowTextW(hwnd, buffer, len(buffer))
    return buffer.value


def windows(pids):
    result = []
    @callback
    def visit(hwnd, _):
        pid = W.DWORD()
        u.GetWindowThreadProcessId(hwnd, C.byref(pid))
        if pid.value in pids and u.IsWindowVisible(hwnd):
            parts = [text(hwnd)]
            @callback
            def child(item, _):
                parts.append(text(item))
                return True
            u.EnumChildWindows(hwnd, child, 0)
            result.append((hwnd, '\n'.join(parts)))
        return True
    u.EnumWindows(visit, 0)
    return result


def live_fixture_process(pid, folder, terminate=False):
    handle = k.OpenProcess(0x1000 | 1, False, pid)
    if not handle:
        return False
    try:
        code = W.DWORD()
        if not k.GetExitCodeProcess(handle, C.byref(code)) or code.value != 259:
            return False
        buffer = C.create_unicode_buffer(32768)
        size = W.DWORD(len(buffer))
        if not k.QueryFullProcessImageNameW(handle, 0, buffer, C.byref(size)):
            return False
        inside = str(Path(buffer.value).parent).casefold() == str(folder).casefold()
        if inside and terminate:
            k.TerminateProcess(handle, 99)
        return inside
    finally:
        k.CloseHandle(handle)


def fixture(publish, root, name, config=None):
    folder = (root / name).resolve()
    folder.mkdir(parents=True, exist_ok=False)
    for source in publish.rglob('*'):
        if not source.is_file():
            continue
        relative = source.relative_to(publish)
        # Also accept an installed package safely: private config/data must never be hard-linked.
        if relative.parts[0] in {'Source', 'Data', 'pharmacyApp.exe.WebView2'}:
            continue
        if relative.name in {'config.json', 'config.jsonc', 'opening.md'}:
            continue
        dest = folder / relative
        dest.parent.mkdir(parents=True, exist_ok=True)
        os.link(source, dest)
    (folder / 'config.json').write_text(json.dumps({
        'Application': {}, 'Database': {'Hostname': '127.0.0.1', 'Port': 1, 'DatabaseName': 'Portfolio',
                                       'Username': 'startup_test', 'Password': 'not-a-real-password'},
        'Ai': {}, 'Secrets': {}}), encoding='utf-8')
    if config:
        shutil.copyfile(config, folder / 'config.json')
    (folder / 'opening.md').write_text('# Enter the app\n', encoding='utf-8')
    return folder


def verify(folder, expected, env=None, healthy=False, timeout=25, checks=None, executable="pharmacyApp.exe", arguments=()):
    process = subprocess.Popen([str(folder / executable), *arguments], cwd=folder,
                               env=dict(os.environ, **(env or {})))
    owned = {process.pid}
    try:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            owned |= descendants(process.pid)
            shown = windows(owned)
            match = next(((hwnd, content) for hwnd, content in shown
                          if ('Pharmacy - Startup error' in content) or (healthy and content.startswith('Pharmacy\n'))), None)
            if match:
                hwnd, content = match
                assert all(part.casefold() in content.casefold() for part in expected), \
                    'Dialog did not contain expected recovery text: ' + repr(expected)
                if not healthy:
                    assert text(u.GetDlgItem(hwnd, 100)) == expected[0], 'Unexpected error heading'
                    assert 'Details:' not in content and 'Application folder:' not in content, 'Obsolete error details remain'
                    for index, (label, passed) in enumerate(checks or []):
                        marker = u.GetDlgItem(hwnd, 200 + index)
                        assert text(u.GetDlgItem(hwnd, 300 + index)) == label, 'Incorrect checklist label'
                        symbol = text(marker)
                        assert symbol in {'\u2713', '\u2717'}, 'Checklist marker is not a tick/cross'
                        if passed is not None:
                            assert symbol == ('\u2713' if passed else '\u2717'), 'Incorrect checklist result for ' + label
                        time.sleep(.05)
                        dc = u.GetDC(marker)
                        try:
                            expected_colour = 0x008000 if symbol == '\u2713' else 0x0000ff
                            pixels = [g.GetPixel(dc, x, y) for y in range(26) for x in range(25)]
                            assert pixels.count(expected_colour) >= 2, 'Incorrect rendered status colour for ' + label + ': ' + repr(set(pixels))
                        finally:
                            u.ReleaseDC(marker, dc)
                if healthy:
                    assert 'Startup error' not in content, 'Healthy launch displayed an error'
                    process.wait(timeout=5)
                    assert process.returncode == 0, 'Launcher did not exit after the opening screen became visible'
                    time.sleep(2)
                    assert not any('Startup error' in body for _, body in windows(owned)), 'Opening browser initialization failed'
                    u.PostMessageW(hwnd, 0x10, 0, 0)  # Close before accepting; no administrator session is opened.
                else:
                    u.PostMessageW(hwnd, 0x10, 0, 0)  # Close the actual native OK dialog.
                break
            time.sleep(.05)
        else:
            raise AssertionError('No visible startup result for ' + folder.name)
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline and any(live_fixture_process(pid, folder) for pid in owned):
            time.sleep(.05)
        assert not any(live_fixture_process(pid, folder) for pid in owned), 'Windowless startup process remains'
        print('PASS ' + folder.name, flush=True)
    finally:
        for pid in owned | descendants(process.pid):
            live_fixture_process(pid, folder, terminate=True)


def main():
    args = argparse.ArgumentParser()
    args.add_argument('publish', type=Path)
    args.add_argument('fixtures', type=Path)
    args.add_argument('--healthy-config', type=Path)
    args.add_argument('--include-timeout', action='store_true')
    options = args.parse_args()
    options.fixtures.mkdir(parents=True, exist_ok=True)
    project = ['Project folder may be incomplete, re-pull the repository']
    dependencies = ['Missing Dependincies']
    database = ['Could not connect to database:', 'Check internet', 'Check connection settings at config.json']
    def dependency_rows(failed):
        return [(name, name not in failed) for name in [
            '.NET Framework 4.x (Windows component)', '.NET 10 Desktop Runtime (included)',
            'Microsoft Edge WebView2 Runtime', 'WebView2 libraries (included)', 'Npgsql (included)', 'Markdig (included)']]
    database_rows = [('Check internet', None), ('Check connection settings at config.json', False)]
    folder = fixture(options.publish, options.fixtures, 'missing-companion')
    (folder / 'Npgsql.dll').rename(folder / 'Npgsql.removed')
    verify(folder, dependencies, checks=dependency_rows({'Npgsql (included)'}))
    folder = fixture(options.publish, options.fixtures, 'missing-configuration')
    (folder / 'config.json').rename(folder / 'config.removed')
    verify(folder, project)
    folder = fixture(options.publish, options.fixtures, 'broken-dotnet-host')
    (folder / 'hostfxr.dll').rename(folder / 'hostfxr.original')
    (folder / 'hostfxr.dll').write_bytes(b'broken host')
    verify(folder, dependencies, checks=dependency_rows({'.NET 10 Desktop Runtime (included)'}))
    folder = fixture(options.publish, options.fixtures, 'missing-wpf-dependency')
    (folder / 'System.IO.Packaging.dll').rename(folder / 'System.IO.Packaging.removed')
    verify(folder, dependencies, checks=dependency_rows({'.NET 10 Desktop Runtime (included)'}))
    folder = fixture(options.publish, options.fixtures, 'invalid-configuration')
    (folder / 'config.json').write_text('{ invalid JSON', encoding='utf-8')
    verify(folder, database, checks=database_rows)
    folder = fixture(options.publish, options.fixtures, 'database-unreachable')
    verify(folder, database, checks=database_rows)
    folder = fixture(options.publish, options.fixtures, 'webview-runtime-unavailable')
    verify(folder, dependencies, {'WEBVIEW2_BROWSER_EXECUTABLE_FOLDER': str(folder / 'missing-browser')},
           checks=dependency_rows({'Microsoft Edge WebView2 Runtime'}))
    folder = fixture(options.publish, options.fixtures, 'data-folder-unwritable')
    (folder / 'Data').write_text('A file prevents creating the Data directory.', encoding='utf-8')
    verify(folder, project)
    folder = fixture(options.publish, options.fixtures, 'legacy-config-migration')
    original = '// Preserve this comment.\n' + (folder / 'config.json').read_text(encoding='utf-8')
    (folder / 'config.json').rename(folder / 'config.jsonc')
    (folder / 'config.jsonc').write_text(original, encoding='utf-8')
    before = (folder / 'config.jsonc').read_bytes()
    verify(folder, database, checks=database_rows)
    assert not (folder / 'config.jsonc').exists() and (folder / 'config.json').read_bytes() == before, 'Migration changed config contents'
    # Exercise the shared native renderer with controlled passing/failing statuses.
    folder = fixture(options.publish, options.fixtures, 'status-colour-rendering')
    compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    shared = Path(__file__).resolve().parents[2] / 'Launcher/StartupFailureDialog.cs'
    harness = Path(__file__).with_name('StatusDialogFixture.cs').resolve()
    subprocess.run([str(compiler), '/nologo', '/target:winexe', '/platform:x64',
                    '/out:' + str(folder / 'StatusDialogFixture.exe'), str(shared), str(harness)], check=True)
    verify(folder, database, checks=[('Check internet', True), ('Check connection settings at config.json', False)],
           executable='StatusDialogFixture.exe')
    verify(folder, database, checks=[('Check internet', False), ('Check connection settings at config.json', True)],
           executable='StatusDialogFixture.exe', arguments=('reverse',))
    if options.healthy_config:
        folder = fixture(options.publish, options.fixtures, 'healthy-opening', options.healthy_config)
        verify(folder, ['Pharmacy'], healthy=True)
    if options.include_timeout:
        folder = fixture(options.publish, options.fixtures, 'startup-timeout')
        (folder / 'pharmacyApp.Core.exe').rename(folder / 'original-apphost.exe')
        source = folder / 'NoWindow.cs'
        source.write_text('class NoWindow { static void Main() { System.Threading.Thread.Sleep(-1); } }', encoding='utf-8')
        compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        subprocess.run([str(compiler), '/nologo', '/target:winexe', '/platform:x64',
                        '/out:' + str(folder / 'pharmacyApp.Core.exe'), str(source)], check=True)
        verify(folder, project, timeout=100)


if __name__ == '__main__':
    main()
