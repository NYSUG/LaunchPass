#!/usr/bin/env python3
"""Read-only Device Portal diagnostics; Python standard library, no secret persistence."""
import argparse
import base64
from datetime import datetime, timezone
import getpass
from http.cookiejar import Cookie, CookieJar
from http.cookies import SimpleCookie
import json
import os
import re
import ssl
import sys
import warnings
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import (Request, build_opener, HTTPRedirectHandler,
                            HTTPCookieProcessor, HTTPSHandler, ProxyHandler)


class DiagnosticError(Exception):
    """Only locally defined, credential-free messages belong in this exception."""


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None  # Never forward credentials/cookies to a redirect target.


class Portal:
    def __init__(self, url, username=None, password=None, cookie=None,
                 ca_file=None, insecure=False, timeout=15):
        parsed = urlsplit(url)
        if (parsed.scheme != "https" or not parsed.hostname or parsed.username is not None
                or parsed.password is not None or parsed.path not in ("", "/")
                or parsed.query or parsed.fragment):
            raise DiagnosticError("Use an HTTPS origin only, without credentials, path, query, or fragment.")
        try:
            parsed.port
        except ValueError:
            raise DiagnosticError("Invalid portal port.") from None
        self.origin = url.rstrip("/")
        self.timeout = timeout
        self.cookies = CookieJar()  # Memory only: never load/save a cookie jar.
        self.authorization = None
        if username is not None:
            if ":" in username:
                raise DiagnosticError("Basic-auth username cannot contain a colon.")
            self.authorization = "Basic " + base64.b64encode(
                (username + ":" + password).encode("utf-8")).decode("ascii")
        if cookie is not None:
            values = SimpleCookie()
            try:
                values.load(cookie)
            except Exception:
                raise DiagnosticError("Invalid session-cookie input.") from None
            if not values:
                raise DiagnosticError("Session-cookie input is empty or invalid.")
            for name, value in values.items():
                self.cookies.set_cookie(Cookie(
                    0, name, value.value, None, False, parsed.hostname, False, False,
                    "/", True, True, None, True, None, None, {}, False))
        context = ssl.create_default_context(cafile=ca_file)
        if insecure:
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE
        self.opener = build_opener(ProxyHandler({}), NoRedirect(),
                                   HTTPCookieProcessor(self.cookies), HTTPSHandler(context=context))
        self.csrf_seen = False

    def _get(self, path, params=None):
        if path not in ("/", "/api/app/packagemanager/packages", "/api/filesystem/apps/files"):
            raise DiagnosticError("Endpoint is outside this read-only diagnostic's allowlist.")
        headers = {"Accept": "application/json"}
        if self.authorization:
            headers["Authorization"] = self.authorization
        url = self.origin + path + ("?" + urlencode(params) if params else "")
        request = Request(url, headers=headers, method="GET")
        # Let CookieJar choose cookies valid for this host/path/expiry before mirroring CSRF.
        self.cookies.add_cookie_header(request)
        selected = SimpleCookie()
        selected.load(request.get_header("Cookie", ""))
        if "CSRF-Token" in selected:
            request.add_header("X-CSRF-Token", selected["CSRF-Token"].value)
            self.csrf_seen = True
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                return response.read()
        except HTTPError as error:
            code = error.code
            error.close()
            # Never print server bodies, URLs, headers, cookies, or exception representations.
            raise DiagnosticError("Portal HTTP status " + str(code)) from None
        except (URLError, OSError, ValueError):
            raise DiagnosticError("Portal connection/TLS failure; check address, certificate, and network.") from None

    def bootstrap(self):
        self._get("/")  # Establish authenticated cookies before API access.

    def get_json(self, path, params=None):
        try:
            data = self._get(path, params)
        except DiagnosticError as error:
            if str(error) != "Portal HTTP status 403":
                raise
            self.bootstrap()  # One bounded cookie/CSRF refresh; no login or write endpoint guesses.
            data = self._get(path, params)
        try:
            return json.loads(data)
        except (ValueError, UnicodeError):
            raise DiagnosticError("Portal returned invalid JSON; response body suppressed.") from None


def select_package(payload, full_name=None):
    packages = payload.get("InstalledPackages") if isinstance(payload, dict) else None
    if not isinstance(packages, list) or any(not isinstance(p, dict) for p in packages):
        raise DiagnosticError("Unexpected installed-package response schema.")
    candidates = [p for p in packages if (
        p.get("PackageFullName") == full_name if full_name else
        any("retroarch" in str(p.get(k, "")).casefold()
            for k in ("Name", "PackageFullName", "PackageFamilyName")))]
    if not candidates:
        raise DiagnosticError("No matching RetroArch package; use --package-full-name for renamed builds.")
    if len(candidates) != 1:
        raise DiagnosticError("Multiple matching packages; select one with --package-full-name.")
    package = candidates[0]
    if not isinstance(package.get("PackageFullName"), str) or not package["PackageFullName"]:
        raise DiagnosticError("Selected package has no PackageFullName.")
    result = {k: package.get(k) for k in ("Name", "PackageFullName", "PackageFamilyName")}
    version = package.get("Version")
    if isinstance(version, dict) and all(isinstance(version.get(k), int)
                                        for k in ("Major", "Minor", "Build", "Revision")):
        result["version"] = ".".join(str(version[k]) for k in ("Major", "Minor", "Build", "Revision"))
        result["version_source"] = "Portal Version field (package version, not necessarily RetroArch runtime version)"
    elif isinstance(version, str):
        result["version"] = version
        result["version_source"] = "Portal Version field"
    else:
        match = re.search(r"_(\d+\.\d+\.\d+\.\d+)_", package["PackageFullName"])
        result["version"] = match[1] if match else None
        result["version_source"] = "Derived from package full name" if match else "Not provided"
    return result


def directory_entries(payload):
    items = payload.get("Items") if isinstance(payload, dict) else None
    if not isinstance(items, list) or any(not isinstance(i, dict) or not isinstance(i.get("Name"), str) for i in items):
        raise DiagnosticError("Unexpected directory-listing response schema.")
    # Exclude arbitrary response fields and never fetch file contents.
    return [{k: item[k] for k in ("Name", "FileSize", "Type", "SubPath") if k in item}
            for item in items]


def collect(portal, full_name=None):
    report = {
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "live_query_status": "incomplete", "read_only": True,
        "protocol_activation": "Not attempted; no documented arbitrary-URI endpoint identified.",
        "runtime_rom_path": "Not inferred from Device Portal's package-relative path.",
        "game_loading": "Unverified; requires Xbox-executed probe.",
    }
    try:
        portal.bootstrap()
        report["package"] = select_package(portal.get_json("/api/app/packagemanager/packages"), full_name)
        params = {"knownfolderid": "LocalAppData", "packagefullname": report["package"]["PackageFullName"],
                  "path": "LocalState\\roms\\arcade"}
        entries = directory_entries(portal.get_json("/api/filesystem/apps/files", params))
        report["directory"] = {"package_relative_path": params["path"], "entries": entries,
                               "contains_1941_zip_name": any(i["Name"].casefold() == "1941.zip" for i in entries)}
        report["live_query_status"] = "package_and_directory_queried"
    except DiagnosticError as error:
        report["error"] = str(error)
    report["csrf_cookie_mirrored"] = portal.csrf_seen
    return report


def read_secret(prompt):
    # getpass otherwise falls back to echoed stdin when no secure terminal is available.
    with warnings.catch_warnings():
        warnings.simplefilter("error", getpass.GetPassWarning)
        try:
            return getpass.getpass(prompt)
        except getpass.GetPassWarning:
            raise DiagnosticError("A terminal with hidden input is required.") from None


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default=os.environ.get("XBOX"), help="HTTPS origin shown by Xbox Device Portal (or XBOX environment variable); no credentials")
    parser.add_argument("--auth", choices=("basic", "cookie"), default="basic")
    parser.add_argument("--package-full-name", default=os.environ.get("PKG"), help="Exact package selector for renamed/ambiguous builds (or PKG environment variable)")
    tls = parser.add_mutually_exclusive_group()
    tls.add_argument("--ca-file", help="Trusted portal certificate/CA PEM")
    tls.add_argument("--insecure", action="store_true", help="Explicitly disable TLS certificate verification")
    args = parser.parse_args(argv)
    if not args.url:
        parser.error("Supply --url or the uploader's XBOX environment variable.")
    try:
        # Validate the origin/TLS options before asking for any secrets.
        kwargs = dict(ca_file=args.ca_file, insecure=args.insecure)
        Portal(args.url, **kwargs)
        if args.auth == "basic":
            username = os.environ.get("XBOX_USER")
            password = os.environ.get("XBOX_PASS")
            if username is None:
                username = read_secret("Device Portal username (hidden): ")
            if password is None:
                password = read_secret("Device Portal password (hidden): ")
            portal = Portal(args.url, username=username, password=password, **kwargs)
        else:
            cookie = read_secret("Device Portal session Cookie header value (hidden): ")
            portal = Portal(args.url, cookie=cookie, **kwargs)
        report = collect(portal, args.package_full_name)
        print(json.dumps(report, indent=2, ensure_ascii=True))
        return 0 if report["live_query_status"] == "package_and_directory_queried" else 1
    except (DiagnosticError, OSError, ValueError, EOFError):
        print("Diagnostics could not start; check HTTPS origin, TLS options, and secret input. Details suppressed.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
