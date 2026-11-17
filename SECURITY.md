# Security Policy

## Reporting a vulnerability

Please report a security problem privately, not in a public issue:

- Use GitHub's private vulnerability reporting: open the **Security** tab of this repository and choose **Report a vulnerability**.
- Or send an email to tore@tn-data.se.

This is a small open-source tool maintained by one person, so I cannot promise a fix by a given date, but I read every report.

## What the tool does, and what it does not

Container Layer Explorer is meant to run on your own machine, for your own images:

- It listens on `localhost` only, and it rejects requests with any other host name.
- It reads images from your local Docker. It never pushes, pulls, deletes or changes an image, and it never starts a container.
- It unpacks the image into a temporary folder on your disk and removes that folder when it exits (and cleans up old folders at the next start).
- There are no accounts, no telemetry and no outgoing network calls, apart from the links shown in the page footer.

Do not expose it on a network or put it behind a reverse proxy. It has no authentication, and it shows the files that are inside the images you open, which can include secrets.
