import base64
from email.message import Message
import io
import json
import unittest
import warnings
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.parse import parse_qs, urlsplit
from urllib.request import BaseHandler
from urllib.response import addinfourl

import xbox_diagnostics as diagnostic


PACKAGE = {"Name": "RetroArch", "PackageFullName": "RetroArch_1.2.3.4_x64__synthetic",
           "PackageFamilyName": "RetroArch_synthetic",
           "Version": {"Major": 1, "Minor": 2, "Build": 3, "Revision": 4},
           "RegisteredUsers": [{"UserSID": "must-not-be-reported"}]}


class PortalServer(BaseHandler):
    """In-process HTTP transport; exercises urllib cookie/header processing, no Xbox calls."""
    handler_order = 100

    def __init__(self):
        self.calls = []
        self.fail_once = False
        self.redirect = False
        self.invalid_json = False

    def https_open(self, request):
        self.calls.append(request)
        assert request.get_method() == "GET", "Diagnostic attempted a write"
        path = urlsplit(request.full_url).path
        headers = Message()
        status = 200
        if self.redirect:
            status = 302
            headers["Location"] = "https://other.invalid/"
            body = b""
        elif path == "/":
            headers["Set-Cookie"] = "CSRF-Token=synthetic-token; Path=/; Secure"
            body = b"<html>portal bootstrap</html>"
        elif path == "/api/app/packagemanager/packages":
            if self.fail_once:
                self.fail_once = False
                raise HTTPError(request.full_url, 403, "do-not-print-secret", headers,
                                io.BytesIO(b"private server body"))
            body = json.dumps({"InstalledPackages": [PACKAGE]}).encode()
        else:
            body = json.dumps({"Items": [{"Name": "1941.zip", "FileSize": 123, "Type": 32,
                                           "UnexpectedSecret": "must-not-be-reported"}]}).encode()
        if self.invalid_json:
            body = b"not JSON, could contain secrets"
        response = addinfourl(io.BytesIO(body), headers, request.full_url, status)
        response.msg = "Synthetic response"
        return response


class DiagnosticTests(unittest.TestCase):
    def portal(self, **kwargs):
        portal = diagnostic.Portal("https://xbox.invalid:11443", **kwargs)
        server = PortalServer()
        portal.opener.add_handler(server)
        return portal, server

    def test_basic_cookie_csrf_and_read_only_collection(self):
        portal, server = self.portal(username="synthetic-user", password="synthetic-password")
        report = diagnostic.collect(portal)
        self.assertEqual(report["live_query_status"], "package_and_directory_queried")
        self.assertEqual(report["package"]["version"], "1.2.3.4")
        self.assertTrue(report["directory"]["contains_1941_zip_name"])
        self.assertTrue(report["csrf_cookie_mirrored"])
        auth = "Basic " + base64.b64encode(b"synthetic-user:synthetic-password").decode()
        self.assertTrue(all(req.get_header("Authorization") == auth for req in server.calls))
        for req in server.calls[1:]:
            self.assertIn("CSRF-Token=synthetic-token", req.get_header("Cookie"))
            self.assertEqual(req.get_header("X-csrf-token"), "synthetic-token")
        query = parse_qs(urlsplit(server.calls[-1].full_url).query)
        self.assertEqual(query, {"knownfolderid": ["LocalAppData"],
                                "packagefullname": [PACKAGE["PackageFullName"]],
                                "path": ["LocalState\\roms\\arcade"]})
        output = json.dumps(report)
        for secret in ("synthetic-user", "synthetic-password", "synthetic-token", auth,
                       "must-not-be-reported", "RegisteredUsers", "UnexpectedSecret"):
            self.assertNotIn(secret, output)

    def test_cookie_only_session(self):
        portal, server = self.portal(cookie="Session=synthetic-session; CSRF-Token=synthetic-token")
        self.assertEqual(diagnostic.collect(portal)["live_query_status"], "package_and_directory_queried")
        self.assertIsNone(server.calls[0].get_header("Authorization"))
        self.assertIn("Session=synthetic-session", server.calls[-1].get_header("Cookie"))

    def test_403_refresh_is_bounded(self):
        portal, server = self.portal()
        server.fail_once = True
        self.assertEqual(diagnostic.collect(portal)["live_query_status"], "package_and_directory_queried")
        self.assertEqual([urlsplit(r.full_url).path for r in server.calls].count("/"), 2)
        with patch.object(portal, "_get", side_effect=diagnostic.DiagnosticError("Portal HTTP status 403")) as get:
            with self.assertRaises(diagnostic.DiagnosticError):
                portal.get_json("/api/app/packagemanager/packages")
            self.assertEqual(get.call_count, 2)  # Initial API call and failed refresh; no loop.

    def test_redirect_does_not_forward_auth(self):
        portal, server = self.portal(username="synthetic", password="synthetic")
        server.redirect = True
        report = diagnostic.collect(portal)
        self.assertEqual(report["error"], "Portal HTTP status 302")
        self.assertEqual(len(server.calls), 1)

    def test_errors_do_not_expose_server_text(self):
        portal, server = self.portal()
        server.invalid_json = True
        report = diagnostic.collect(portal)
        self.assertIn("invalid JSON", report["error"])
        self.assertNotIn("could contain secrets", json.dumps(report))

    def test_network_failure_redacted(self):
        portal, _ = self.portal()
        with patch.object(portal.opener, "open", side_effect=OSError("secret-password")):
            self.assertNotIn("secret-password", json.dumps(diagnostic.collect(portal)))

    def test_origin_validation(self):
        for url in ("http://xbox.invalid", "https://user:pass@xbox.invalid", "https://xbox.invalid/path",
                    "https://xbox.invalid/?secret=value", "https://xbox.invalid/#secret", "https://xbox.invalid:bad"):
            with self.subTest(url=url), self.assertRaises(diagnostic.DiagnosticError):
                diagnostic.Portal(url)

    def test_no_write_or_arbitrary_endpoint(self):
        portal, server = self.portal()
        for path in ("/api/taskmanager/app", "https://other.invalid/", "/api/filesystem/apps/file"):
            with self.assertRaises(diagnostic.DiagnosticError):
                portal._get(path)
        self.assertEqual(server.calls, [])

    def test_multiple_packages_require_selector(self):
        other = dict(PACKAGE, PackageFullName="RetroArch_2.0.0.0_x64__synthetic")
        payload = {"InstalledPackages": [PACKAGE, other]}
        with self.assertRaises(diagnostic.DiagnosticError):
            diagnostic.select_package(payload)
        self.assertEqual(diagnostic.select_package(payload, other["PackageFullName"])["PackageFullName"], other["PackageFullName"])

    def test_renamed_package_and_missing_match(self):
        renamed = dict(PACKAGE, Name="Renamed", PackageFamilyName="renamed", PackageFullName="Renamed_1.0.0.0_x64__synthetic")
        payload = {"InstalledPackages": [renamed]}
        with self.assertRaises(diagnostic.DiagnosticError):
            diagnostic.select_package(payload)
        self.assertEqual(diagnostic.select_package(payload, renamed["PackageFullName"])["Name"], "Renamed")

    def test_versions_and_schemas(self):
        package = dict(PACKAGE)
        del package["Version"]
        self.assertEqual(diagnostic.select_package({"InstalledPackages": [package]})["version"], "1.2.3.4")
        self.assertEqual(diagnostic.directory_entries({"Items": []}), [])
        for payload in ({}, {"InstalledPackages": [None]}, {"InstalledPackages": []}):
            with self.assertRaises(diagnostic.DiagnosticError):
                diagnostic.select_package(payload)
        for payload in ({}, {"Items": [None]}, {"Items": [{"Name": 1}]}):
            with self.assertRaises(diagnostic.DiagnosticError):
                diagnostic.directory_entries(payload)

    def test_partial_report_keeps_package_if_directory_fails(self):
        portal, _ = self.portal()
        original = portal.get_json
        def get(path, params=None):
            if path.endswith("/files"):
                raise diagnostic.DiagnosticError("Portal HTTP status 404")
            return original(path, params)
        with patch.object(portal, "get_json", side_effect=get):
            report = diagnostic.collect(portal)
        self.assertEqual(report["package"]["version"], "1.2.3.4")
        self.assertEqual(report["live_query_status"], "incomplete")
        self.assertNotIn("directory", report)

    def test_no_echoed_secret_fallback(self):
        def fallback(prompt):
            warnings.warn("Cannot hide input", diagnostic.getpass.GetPassWarning)
            self.fail("Should stop before reading echoed input")
        with patch.object(diagnostic.getpass, "getpass", side_effect=fallback):
            with self.assertRaises(diagnostic.DiagnosticError):
                diagnostic.read_secret("Synthetic prompt")

    def test_cli_report_and_exit_status(self):
        portal, _ = self.portal()
        stdout = io.StringIO()
        with patch.dict(diagnostic.os.environ, {}, clear=True), \
                patch.object(diagnostic, "Portal", return_value=portal), \
                patch.object(diagnostic, "read_secret", side_effect=["synthetic", "synthetic"]), \
                patch("sys.stdout", stdout):
            self.assertEqual(diagnostic.main(["--url", "https://xbox.invalid"]), 0)
        self.assertEqual(json.loads(stdout.getvalue())["live_query_status"], "package_and_directory_queried")

    def test_uploader_environment_compatibility(self):
        portal, _ = self.portal()
        values = {"XBOX": "https://xbox.invalid:11443", "XBOX_USER": "synthetic-user",
                  "XBOX_PASS": "synthetic-password", "PKG": PACKAGE["PackageFullName"]}
        stdout = io.StringIO()
        with patch.dict(diagnostic.os.environ, values, clear=True), \
                patch.object(diagnostic, "Portal", return_value=portal) as client, \
                patch.object(diagnostic, "read_secret") as prompt, \
                patch("sys.stdout", stdout):
            self.assertEqual(diagnostic.main([]), 0)
            prompt.assert_not_called()
            self.assertEqual(client.call_args.args[0], values["XBOX"])
            self.assertEqual(client.call_args.kwargs["username"], values["XBOX_USER"])
            self.assertEqual(client.call_args.kwargs["password"], values["XBOX_PASS"])
        self.assertNotIn(values["XBOX_PASS"], stdout.getvalue())
        self.assertNotIn(values["XBOX_USER"], stdout.getvalue())


if __name__ == "__main__":
    unittest.main()
