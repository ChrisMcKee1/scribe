import Foundation

/// The AI cleanup page. Windows: `SettingsWindow.xaml` lines 1301 to 1812, `SettingsWindow.xaml.cs`,
/// `SettingsWindow.LocalApps.cs`. The disclosure card, the model tuning and the Custom API text already live in
/// `CleanupDisclosure`, `LocalModelTuningText` and `CustomAPIStyleText` and are not repeated here.
struct AiCleanupCopy: CopyCatalog {
    let prefix = "aiCleanup"

    // Page
    let subtitle = CopyItem.same(
        "aiCleanup.subtitle",
        "Optional. An AI model fixes punctuation, grammar and repeated words before Scribe types. Dictation works "
            + "without it.")
    let use = CopyItem.same("aiCleanup.use", "Use AI cleanup")
    let offHint = CopyItem.same(
        "aiCleanup.offHint", "Off. Scribe types what it hears, with your dictionary and snippets.")
    let turnOnHint = CopyItem.same(
        "aiCleanup.turnOnHint", "Turn on AI cleanup to choose where it runs and set your writing style.")
    let shortcutOn = CopyItem.same("aiCleanup.shortcutOn", "Types your words with AI cleanup.")
    let shortcutOff = CopyItem.same("aiCleanup.shortcutOff", "Types your words. AI cleanup is off.")
    let plainShortcutOff = CopyItem.same(
        "aiCleanup.plainShortcutOff", "AI cleanup is off, so this works like the dictation shortcut.")

    // Where it runs
    let whereItRuns = CopyItem.same("aiCleanup.whereItRuns", "Where AI cleanup runs")
    let onThisMac = CopyItem.changed("aiCleanup.onThisMac", "On this Mac", windows: "On this PC", because: .thisMac)
    let onThisMacHint = CopyItem.changed(
        "aiCleanup.onThisMacHint",
        "Private: your text stays on this Mac. Use Foundry Local, Ollama or LM Studio if you have them.",
        windows: "Private: your text stays on this PC. Scribe can set it up for you, or use Ollama or LM Studio if "
            + "you have them.",
        because: .foundryLocal)
    let copilot = CopyItem.same("aiCleanup.copilot", "GitHub Copilot")
    let copilotUnavailable = CopyItem.added(
        "aiCleanup.copilotUnavailable",
        "Scribe's GitHub Copilot integration is unavailable on Mac",
        because: .copilotUnavailable)
    let foundry = CopyItem.same("aiCleanup.foundry", "Microsoft Foundry")
    let foundryHint = CopyItem.same(
        "aiCleanup.foundryHint", "Uses a model in your Azure account. Your text goes to your Azure resource.")
    let anotherService = CopyItem.same("aiCleanup.anotherService", "Another AI service")
    let anotherServiceHint = CopyItem.same(
        "aiCleanup.anotherServiceHint",
        "Connects to the server address you enter, such as OpenRouter, OpenAI or a server on another computer. Your "
            + "text goes to that address.")
    let disclosureTitle = CopyItem.same("aiCleanup.disclosureTitle", "What AI cleanup sends")

    // On this Mac
    let howToRun = CopyItem.same("aiCleanup.howToRun", "How to run it")
    let foundryLocal = CopyItem.changed(
        "aiCleanup.foundryLocal", "Foundry Local", windows: "Let Scribe manage it", because: .foundryLocal)
    let foundryLocalHint = CopyItem.changed(
        "aiCleanup.foundryLocalHint",
        "Uses Foundry Local, which you install once. The model downloads the first time you use it.",
        windows: "Scribe downloads the model and manages its memory for you. Setup can use several GB.",
        because: .foundryLocal)
    let ollama = CopyItem.same("aiCleanup.ollama", "Ollama")
    let ollamaHint = CopyItem.same("aiCleanup.ollamaHint", "Uses a model you downloaded in Ollama.")
    let lmStudio = CopyItem.same("aiCleanup.lmStudio", "LM Studio")
    let lmStudioHint = CopyItem.same("aiCleanup.lmStudioHint", "Uses a model you downloaded in LM Studio.")
    let model = CopyItem.same("aiCleanup.model", "Model")
    let foundryAliasHint = CopyItem.added("aiCleanup.foundryAliasHint", "The name Foundry Local gives the model.")
    let foundryInstall = CopyItem.added(
        "aiCleanup.foundryInstall",
        "Install Foundry Local once with brew install microsoft/foundrylocal/foundrylocal in Terminal.")
    let nothingDownloads = CopyItem.changed(
        "aiCleanup.nothingDownloads",
        "Scribe downloads nothing itself. Foundry Local downloads the model the first time you use it.",
        windows: "Nothing downloads until you choose Set up or Load, or save with AI cleanup on. Choosing somewhere "
            + "else for AI cleanup removes what Scribe downloaded for it.",
        because: .staleWindowsCorrected)
    let modelSettings = CopyItem.same("aiCleanup.modelSettings", "Model settings")
    let modelSettingsHint = CopyItem.same(
        "aiCleanup.modelSettingsHint",
        "How much of your vocabulary the model gets. Most people never need to change this.")
    let wholeVocabulary = CopyItem.same("aiCleanup.wholeVocabulary", "Send your whole vocabulary when it fits")
    let ollamaSettings = CopyItem.same("aiCleanup.ollamaSettings", "Ollama settings")
    let lmStudioSettings = CopyItem.same("aiCleanup.lmStudioSettings", "LM Studio settings")
    let contextSize = CopyItem.same("aiCleanup.contextSize", "Context size")
    let loadingModel = CopyItem.same("aiCleanup.loadingModel", "Loading the model...")
    let freeingMemory = CopyItem.same("aiCleanup.freeingMemory", "Freeing model memory...")
    let memoryFreed = CopyItem.same("aiCleanup.memoryFreed", "Model memory freed.")
    let memoryNotFreed = CopyItem.same("aiCleanup.memoryNotFreed", "Couldn't free model memory. Try again.")

    // Microsoft Foundry
    let howToSignIn = CopyItem.same("aiCleanup.howToSignIn", "How to sign in")
    let signInAccount = CopyItem.same("aiCleanup.signInAccount", "Your Azure account (Azure CLI) (recommended)")
    let signInAccountHint = CopyItem.same(
        "aiCleanup.signInAccountHint", "Signs in with your Azure account in the browser. Needs Azure CLI.")
    let signInApp = CopyItem.same("aiCleanup.signInApp", "An app registration (service principal)")
    let signInAppHint = CopyItem.same(
        "aiCleanup.signInAppHint",
        "An app your organization set up in Azure for Scribe. Reliable when your account belongs to several "
            + "organizations.")
    let signInKey = CopyItem.same("aiCleanup.signInKey", "An API key")
    let signInKeyHint = CopyItem.same(
        "aiCleanup.signInKeyHint",
        "A key for the Azure resource that runs your model. Works without signing in to Azure, if your resource "
            + "accepts keys.")
    let tenantOptional = CopyItem.same("aiCleanup.tenantOptional", "Tenant ID (optional)")
    let optional = CopyItem.same("aiCleanup.optional", "Optional")
    let tenantHint = CopyItem.same(
        "aiCleanup.tenantHint",
        "Only needed if your Azure account belongs to more than one organization. You'll find it in the Azure portal "
            + "under Microsoft Entra ID.")
    let subscription = CopyItem.same("aiCleanup.subscription", "Subscription")
    let subscriptionHint = CopyItem.same(
        "aiCleanup.subscriptionHint",
        "Scribe starts with your current Azure subscription. Choose All subscriptions to see models in every "
            + "subscription you can use.")
    let enterManually = CopyItem.same("aiCleanup.enterManually", "Enter details manually")
    let directoryId = CopyItem.same("aiCleanup.directoryId", "Directory (tenant) ID")
    let directoryIdExample = CopyItem.same("aiCleanup.directoryIdExample", "For example, contoso.onmicrosoft.com")
    let clientId = CopyItem.same("aiCleanup.clientId", "Application (client) ID")
    let clientIdHint = CopyItem.same("aiCleanup.clientIdHint", "From the app registration's Overview page")
    let clientSecret = CopyItem.same("aiCleanup.clientSecret", "Client secret")
    let clientSecretHint = CopyItem.same("aiCleanup.clientSecretHint", "The secret's Value, not its ID")
    let clientSecretNote = CopyItem.changed(
        "aiCleanup.clientSecretNote",
        "Saved encrypted in your Keychain. Azure shows a secret's Value only once, when you create it.",
        windows: "Saved encrypted on this PC with your Windows account. Azure shows a secret's Value only once, when "
            + "you create it.",
        because: .keychain)
    let azureAddress = CopyItem.same("aiCleanup.azureAddress", "Endpoint")
    let azureAddressExample = CopyItem.same(
        "aiCleanup.azureAddressExample", "https://my-resource.services.ai.azure.com/")
    let azureAddressHint = CopyItem.same("aiCleanup.azureAddressHint", "Filled in when you choose a model.")
    let deploymentName = CopyItem.same("aiCleanup.deploymentName", "Deployment name")
    let deploymentExample = CopyItem.same("aiCleanup.deploymentExample", "For example, gpt-4o")
    let deploymentHint = CopyItem.same("aiCleanup.deploymentHint", "The model's exact name in Microsoft Foundry.")
    let apiKey = CopyItem.same("aiCleanup.apiKey", "API key")
    let apiKeyPlaceholder = CopyItem.same("aiCleanup.apiKeyPlaceholder", "Paste the key from your Azure resource")
    let apiKeyNote = CopyItem.changed(
        "aiCleanup.apiKeyNote",
        "Saved encrypted in your Keychain.",
        windows: "Saved encrypted on this PC with your Windows account.",
        because: .keychain)
    let apiKeyAddressNote = CopyItem.same(
        "aiCleanup.apiKeyAddressNote",
        "With an API key, Scribe uses your resource's main address instead of the project address. That's expected.")
    let promptCaching = CopyItem.same("aiCleanup.promptCaching", "Let Microsoft Foundry cache what Scribe sends")
    let signedInToAzure = CopyItem.same("aiCleanup.signedInToAzure", "Signed in to Azure.")
    let signedInAs = CopyItem.same("aiCleanup.signedInAs", "Signed in as {account}.")
    let notSignedIn = CopyItem.same(
        "aiCleanup.notSignedIn", "Not signed in to Azure. Until you sign in, Scribe types what it heard.")
    let checkingSignIn = CopyItem.same("aiCleanup.checkingSignIn", "Checking your Azure sign-in...")
    let finishSigningIn = CopyItem.same("aiCleanup.finishSigningIn", "Finish signing in in your browser.")
    let signInTimedOut = CopyItem.same("aiCleanup.signInTimedOut", "Azure sign-in timed out. Please try again.")
    let signInBeforeBrowsing = CopyItem.same("aiCleanup.signInBeforeBrowsing", "Sign in before browsing deployments.")
    let cliMissing = CopyItem.changed(
        "aiCleanup.cliMissing",
        "Azure CLI isn't installed. In Terminal, run brew install azure-cli.",
        windows: "Azure CLI isn't installed.",
        because: .macSystemFeature)
    let couldNotListDeployments = CopyItem.same(
        "aiCleanup.couldNotListDeployments",
        "Couldn't list deployments. Sign in again and make sure you have access to a deployment.")

    // Another AI service
    let serverAddress = CopyItem.same("aiCleanup.serverAddress", "Server address")
    let serverAddressExample = CopyItem.same("aiCleanup.serverAddressExample", "https://openrouter.ai/api/v1")
    let apiStyle = CopyItem.same("aiCleanup.apiStyle", "API")
    let modelName = CopyItem.same("aiCleanup.modelName", "Model name")
    let modelNameHint = CopyItem.same("aiCleanup.modelNameHint", "The name exactly as the service lists it.")
    let serviceKey = CopyItem.same("aiCleanup.serviceKey", "API key (optional)")
    let serviceKeyHint = CopyItem.changed(
        "aiCleanup.serviceKeyHint",
        "Enter a key if your service needs one. Saved encrypted in your Keychain.",
        windows: "Enter a key if your service needs one. Saved encrypted on this PC with your Windows account.",
        because: .keychain)

    // Test and status
    let testConnection = CopyItem.same("aiCleanup.testConnection", "Test connection")
    let changedSinceTest = CopyItem.same(
        "aiCleanup.changedSinceTest", "Changed since the last test. Choose Test connection.")
    let fillInDetails = CopyItem.same(
        "aiCleanup.fillInDetails", "Fill in the details above, then choose Test connection.")

    // Writing style and instructions
    let writingStyle = CopyItem.same("aiCleanup.writingStyle", "Writing style")
    let writingStyleHint = CopyItem.same(
        "aiCleanup.writingStyleHint",
        "Scribe's style: clear sentences, lists when you list things, correct punctuation, numbers as digits, and "
            + "your spoken corrections applied.")
    let customize = CopyItem.same("aiCleanup.customize", "Customize")
    let restoreStyle = CopyItem.same("aiCleanup.restoreStyle", "Restore Scribe's style")
    let advancedTitle = CopyItem.same("aiCleanup.advancedTitle", "Advanced AI settings")
    let advancedHint = CopyItem.same(
        "aiCleanup.advancedHint", "The instructions Scribe gives the AI model. Most people never need to change them.")
    let instructionsTitle = CopyItem.same("aiCleanup.instructionsTitle", "Instructions for the AI model")
    let instructionsAutomatic = CopyItem.changed(
        "aiCleanup.instructionsAutomatic",
        "Automatic uses short instructions for models on this Mac and detailed instructions for cloud models.",
        windows: "Automatic uses short instructions for models on this PC and detailed instructions for cloud models.",
        because: .thisMac)
    let instructionsNote = CopyItem.same(
        "aiCleanup.instructionsNote",
        "Changes what Scribe tells the AI model. Leave a box on Scribe's text to keep getting its improvements.")
    let detailedInstructions = CopyItem.same("aiCleanup.detailedInstructions", "Detailed instructions")
    let restoreDetailed = CopyItem.same("aiCleanup.restoreDetailed", "Restore Scribe's detailed instructions")
    let shortInstructions = CopyItem.same("aiCleanup.shortInstructions", "Short instructions")
    let restoreShort = CopyItem.same("aiCleanup.restoreShort", "Restore Scribe's short instructions")
    let styleDetailed = CopyItem.same("aiCleanup.styleDetailed", "Detailed, for cloud and larger models")
    let styleShort = CopyItem.changed(
        "aiCleanup.styleShort",
        "Short, for small models on this Mac",
        windows: "Short, for small models on this PC",
        because: .thisMac)
}
