(function () {
    function resolve(pref) {
        if (pref === 'system') {
            return (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches)
                ? 'dark'
                : 'light';
        }
        return pref === 'light' ? 'light' : 'dark';
    }

    function apply(pref) {
        var resolved = resolve(pref);
        document.documentElement.setAttribute('data-theme', resolved);
        try { localStorage.setItem('fs.themePref', pref || 'dark'); } catch (e) { /* ignore */ }
        return resolved;
    }

    window.fsTheme = { resolve: resolve, apply: apply };

    // Paint with the last-known preference immediately (before the Blazor circuit
    // connects and fetches the authoritative per-project value) to avoid a flash.
    try {
        apply(localStorage.getItem('fs.themePref') || 'dark');
    } catch (e) {
        document.documentElement.setAttribute('data-theme', 'dark');
    }
})();
