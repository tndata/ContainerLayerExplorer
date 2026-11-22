# Security Policy

## Reporting a vulnerability

Please report a security problem privately, not in a public issue:

- Use GitHub's private vulnerability reporting: open the **Security** tab of this repository and choose **Report a vulnerability**.
- Or send an email to tore@tn-data.se.

This is a small open-source tool maintained by one person, so I cannot promise a fix by a given date, but I read every report.

## What the tool does, and what it does not

Container Layer Explorer is meant to run on your own machine, for your own images:

- It listens on `localhost` (`http://localhost:5000` by default), and it rejects requests with any other host name.
- It only reads from Docker. It never starts a container, and it never changes, deletes, pushes or pulls an image.
- It writes to your disk. To open an image, it exports it from Docker into a folder under your temp folder. That copy holds everything in the image, so it can contain sensitive files. The folder is removed when the tool shuts down normally. If the tool was stopped abruptly, the folder stays until a later start, which removes unused folders that are more than a day old.
- There are no accounts, no telemetry and no outgoing network calls, apart from the links shown in the page footer.

Do not expose it on a network or put it behind a reverse proxy. It has no authentication, and it shows the files that are inside the images you open, which can include secrets.
