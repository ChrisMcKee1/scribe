# Scribe 0.4.0

You can now polish your dictation with your own GitHub Copilot subscription, without standing up
Azure or downloading a model. That is the reason this is 0.4 rather than 0.3.18. The rest of the
release is about cloud models that think before they answer: Scribe was cutting them off mid-thought
and then quietly using your raw transcript instead.

## Bring your own GitHub Copilot licence

There is a fourth option under Settings, AI cleanup, Provider: **GitHub Copilot**. Pick it and Scribe
uses the Copilot CLI you already have installed and signed in, so the models on offer are whichever
ones your GitHub account is entitled to. There is no endpoint to paste and no key to store.

The panel checks for the CLI and tells you what it found, including the version and where it is. If
it is missing, there is a button to install it and another to sign in, both of which open a terminal
you can watch rather than doing something invisible in the background. The model list fills itself in
when you choose the provider, and shows the reasoning levels each model supports.

Two things worth knowing before you switch to it. Starting the session takes about twenty seconds,
once, after you save; dictations during that window use your raw transcript and say so. And each
cleanup is slower and costs more Copilot usage than the other providers, because Copilot sends its
full coding-assistant context with every request. On quality it is level with the alternatives.

Scribe does not enable any of Copilot's file, shell or network abilities, and never sees or stores a
GitHub token.

## AI cleanup works with reasoning models now

If you pointed Scribe at a reasoning deployment, cleanup could sit on "getting ready" forever, or
appear to do nothing at all. Neither was obvious, because dictation kept working: Scribe falls back
to your raw words whenever cleanup cannot answer, which is the right behaviour and a very good
disguise.

The cause was a budget. These models spend tokens thinking before they write anything, and that
thinking counts against the same allowance as the answer. Scribe's allowance was set for models that
answer immediately, so the model spent it all on thought and had nothing left to reply with. One
model used over five hundred tokens of thinking to punctuate a single sentence. Startup checks and
real cleanups both have room now, and the wait before a slow deployment is declared unusable is
longer.

A second problem sat behind it. Some models on Microsoft Foundry, including MAI-Thinking-1, do not
answer on the newer of the two APIs Scribe can use. Scribe now notices the refusal and asks again the
other way, so those deployments work without any setting to find.

## Smaller things

- The startup message for a provider that is connecting no longer says "Downloading". Nothing was
  being downloaded.
- Saving settings twice in quick succession no longer restarts a connection that was already being
  made, which used to mean waiting for it twice.
- Scribe reports which model handled a session correctly in its diagnostics for every provider.
- The highlight-and-rewrite tool has been removed. It was experimental, and Windows now has its own
  writing assistant for the same job. Dictation cleanup is unaffected; this was the separate feature
  that rewrote text you had already selected in another app.
