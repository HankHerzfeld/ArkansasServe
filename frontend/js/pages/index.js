
  Auth.init();
  document.getElementById('btn-hero-login').addEventListener('click', () => Auth.login());
  document.getElementById('btn-hero-signup')?.addEventListener('click', () => Auth.signUp());

  // ── Pre-login nav drawer ──────────────────────────────────────────────────
  // Mirrors ui.js's drawer for the signed-in shell. Duplicated rather than shared
  // because the landing page deliberately doesn't load the app shell (ui.js needs
  // api.js/scope.js), and a marketing page shouldn't pull those in for one toggle.
  // Kept in this file rather than inline because the CSP allows script-src 'self' only.
  (() => {
    const nav = document.querySelector('.navbar');
    const toggle = document.getElementById('btn-nav-toggle');
    const backdrop = document.getElementById('nav-backdrop');
    const menu = document.getElementById('nav-menu');
    if (!nav || !toggle || !menu) return;

    function setOpen(open) {
      nav.classList.toggle('nav-open', open);
      toggle.setAttribute('aria-expanded', String(open));
      toggle.setAttribute('aria-label', open ? 'Close menu' : 'Open menu');
      document.body.style.overflow = open ? 'hidden' : '';
      if (open) menu.querySelector('a, button')?.focus();
    }

    toggle.addEventListener('click', () => setOpen(!nav.classList.contains('nav-open')));
    backdrop?.addEventListener('click', () => setOpen(false));
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && nav.classList.contains('nav-open')) { setOpen(false); toggle.focus(); }
    });
    // Jump links and the two CTAs should all close the drawer behind them.
    menu.addEventListener('click', (e) => { if (e.target.closest('a, button')) setOpen(false); });
  })();

  // If already logged in, redirect to the role-appropriate portal.
  // (MSAL initializes asynchronously — wait for it before reading session state.)
  Auth.ready().then(() => {
    if (Auth.isAuthenticated()) {
      const profile = Auth.getProfile();
      const destinations = {
        Student:           '/dashboard.html',
        EventAdmin:        '/org-portal.html',
        GroupAdmin:        '/org-portal.html',
        OrganizationAdmin: '/admin-portal.html',
        SuperAdmin:        '/platform-admin.html',
      };
      window.location.href = destinations[profile?.adminLevel] || '/dashboard.html';
    }
  });
