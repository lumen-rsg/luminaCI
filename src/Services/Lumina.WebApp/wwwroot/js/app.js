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

        source.onopen = function () {
            dotNetRef.invokeMethodAsync('OnStreamStateChanged', 'live');
        };

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
            if (source.readyState === EventSource.CLOSED) {
                window.buildLogStream = null;
                dotNetRef.invokeMethodAsync('OnStreamEnd');
            } else {
                // EventSource reconnects automatically after transient failures.
                dotNetRef.invokeMethodAsync('OnStreamStateChanged', 'reconnecting');
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

    window.scrollBuildLogsToEnd = function (elementId) {
        var element = document.getElementById(elementId);
        if (element) {
            element.scrollTop = element.scrollHeight;
        }
    };

    window.copyTextToClipboard = async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Clipboard API can be unavailable in non-secure development
            // contexts. Keep the action functional without retaining the text.
            var input = document.createElement('textarea');
            input.value = text;
            input.setAttribute('readonly', '');
            input.style.position = 'fixed';
            input.style.opacity = '0';
            document.body.appendChild(input);
            input.select();
            var copied = document.execCommand('copy');
            document.body.removeChild(input);
            return copied;
        }
    };

    window.downloadTextFile = function (filename, text) {
        var blobUrl = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
        var link = document.createElement('a');
        link.href = blobUrl;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(blobUrl);
    };

    // --- Accessible dialogs -------------------------------------------------
    // Blazor conditionally renders dialog nodes. This helper moves focus into
    // the dialog, traps Tab/Shift+Tab, maps Escape to its close action, and
    // restores focus to the opener when the node is removed.
    window.activateAccessibleDialog = function (elementId) {
        var dialog = document.getElementById(elementId);
        if (!dialog || dialog.dataset.a11yActive === 'true') {
            return;
        }

        dialog.dataset.a11yActive = 'true';
        var opener = document.activeElement;
        var focusableSelector =
            'button:not([disabled]), [href], input:not([disabled]), select:not([disabled]), ' +
            'textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

        dialog.addEventListener('keydown', function (event) {
            if (event.key === 'Escape') {
                event.preventDefault();
                var closeButton = dialog.querySelector('[data-dialog-close]');
                if (closeButton && !closeButton.disabled) {
                    closeButton.click();
                }
                return;
            }

            if (event.key !== 'Tab') {
                return;
            }

            var focusable = Array.from(dialog.querySelectorAll(focusableSelector))
                .filter(function (element) {
                    return element.getClientRects().length > 0;
                });
            if (focusable.length === 0) {
                event.preventDefault();
                dialog.focus();
                return;
            }

            var first = focusable[0];
            var last = focusable[focusable.length - 1];
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        });

        var observer = new MutationObserver(function () {
            if (!document.body.contains(dialog)) {
                observer.disconnect();
                if (opener && document.body.contains(opener)) {
                    opener.focus();
                }
            }
        });
        observer.observe(document.body, { childList: true, subtree: true });

        var firstFocusable = dialog.querySelector('[autofocus]') || dialog.querySelector(focusableSelector);
        dialog.setAttribute('tabindex', '-1');
        (firstFocusable || dialog).focus();
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
