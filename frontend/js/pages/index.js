
  Auth.init();
  document.getElementById('btn-hero-login').addEventListener('click', () => Auth.login());
  document.getElementById('btn-hero-signup')?.addEventListener('click', () => Auth.signUp());

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
