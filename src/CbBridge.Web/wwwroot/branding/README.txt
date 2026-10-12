Put your company's logo (and optional favicon) in this folder, then point to it in appsettings.json:

  "Branding": {
    "CompanyName": "Your Company",
    "LogoUrl": "branding/your-logo.svg",
    "FaviconUrl": "branding/favicon.png",
    "AccentColor": "#1F4E8C",
    "HeaderColor": "#14202E"
  }

The logo always sits on HeaderColor (top bar and sign-in band): use a light/white version (SVG or PNG with transparent background).
example-logo.svg is a fictional sample — delete it when you add your own.
Full guide: docs/branding.md
