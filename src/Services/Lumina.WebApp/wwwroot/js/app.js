/* Lumina WebApp client scripts.
 *
 * All browser-side JS lives here so the page can ship a strict Content-Security-
 * Policy without 'unsafe-inline' for script-src (SEC-05). Nothing in this file
 * ever reads a bearer token: the JWT now lives in HttpOnly, Secure,
 * SameSite=Strict cookies that the browser attaches automatically for same-
 * origin requests (EventSource + fetch included). An XSS that runs in the
 * rendered DOM therefore has no token to exfiltrate.
 */

(function () {
    'use strict';

    // --- Theme (prevents flash of wrong theme) -------------------------------
    // Runs before Blazor to set the bootstrap theme attribute from the stored
    // preference. Theme is non-sensitive UI state, so localStorage is fine here.
    var theme = localStorage.getItem('lumina_theme');
    if (!theme) {
        theme = window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
    }
    document.documentElement.setAttribute('data-bs-theme', theme);

    // Named entry point invoked from C# (ThemeService) — CSP-safe, no eval.
    window.setLuminaTheme = function (theme) {
        document.documentElement.setAttribute('data-bs-theme', theme);
    };

    // --- SSE build-log streaming --------------------------------------------
    // EventSource cannot set custom headers, but it DOES send cookies for
    // same-origin requests. The gateway promotes the lumina_access cookie to an
    // Authorization: Bearer header before JWT validation, so the stream is
    // authenticated without a token in the URL (which used to leak it via
    // server/proxy access logs and Referer).
    window.buildLogStream = null;

    window.connectBuildLogStream = function (buildId, dotNetRef) {
        if (window.buildLogStream) {
            window.buildLogStream.close();
            window.buildLogStream = null;
        }

        var url = '/api/builds/' + buildId + '/logs/stream';
        var source = new EventSource(url);
        window.buildLogStream = source;

        source.onmessage = function (event) {
            var data = event.data;
            if (data === '[STREAM_END]') {
                source.close();
                window.buildLogStream = null;
                dotNetRef.invokeMethodAsync('OnStreamEnd');
                return;
            }
            dotNetRef.invokeMethodAsync('OnLogLine', data);
        };

        source.onerror = function () {
            // EventSource auto-reconnects; only treat CLOSED as terminal.
            if (source.readyState === EventSource.CLOSED) {
                window.buildLogStream = null;
                dotNetRef.invokeMethodAsync('OnStreamEnd');
            }
        };

        return true;
    };

    window.disconnectBuildLogStream = function () {
        if (window.buildLogStream) {
            window.buildLogStream.close();
            window.buildLogStream = null;
        }
    };

    // --- Authenticated file download ----------------------------------------
    // Cookies are attached automatically; no token in JS, no Authorization header.
    window.downloadFileWithAuth = async function (url, fallbackName) {
        try {
            var resp = await fetch(url);
            if (resp.status === 401) {
                alert('Your session has expired. Please log in again.');
                window.location.replace('/login');
                return false;
            }
            if (!resp.ok) {
                alert('Download failed: ' + resp.status + ' ' + resp.statusText);
                return false;
            }
            var blob = await resp.blob();
            // Extract filename from Content-Disposition, or use the fallback.
            var disposition = resp.headers.get('Content-Disposition');
            var filename = fallbackName || 'download';
            if (disposition) {
                var match = disposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
                if (match && match[1]) {
                    filename = match[1].replace(/['"]/g, '');
                }
            }
            var blobUrl = URL.createObjectURL(blob);
            var a = document.createElement('a');
            a.href = blobUrl;
            a.download = filename;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(blobUrl);
            return true;
        } catch (err) {
            alert('Download error: ' + err.message);
            return false;
        }
    };
})();
