; admin-discord-mcp installer - Inno Setup script
;
; Build: ISCC.exe installer\admin-discord-mcp.iss
;        Override version: ISCC.exe /DAppVersion=1.0.1 installer\admin-discord-mcp.iss
;
; Inputs (must exist before ISCC runs):
;   - bin/win-x64/admin-discord-mcp.exe   (from `dotnet publish`)
;
; Output:
;   - bin/installer/AdminDiscordMcpSetup-<version>-win-x64.exe

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define TaskName "admin-discord-mcp-server"
#define McpName "discord-admin"
#define McpPort "47823"
#define McpUrl "http://127.0.0.1:" + McpPort + "/mcp"
#define TokenHeader "X-Discord-Bot-Token"

[Setup]
AppId={{A3F1C9E2-7D4B-4E6A-9C18-2B5D8F0A1E37}
AppName=admin-discord-mcp
AppVersion={#AppVersion}
AppPublisher=Chloe Dream
AppPublisherURL=https://github.com/chloe-dream/admin-discord-mcp-bot
AppSupportURL=https://github.com/chloe-dream/admin-discord-mcp-bot/issues
AppUpdatesURL=https://github.com/chloe-dream/admin-discord-mcp-bot/releases
DefaultDirName={localappdata}\Programs\admin-discord-mcp
DefaultGroupName=admin-discord-mcp
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\bin\installer
OutputBaseFilename=AdminDiscordMcpSetup-{#AppVersion}-win-x64
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\admin-discord-mcp.exe
UninstallDisplayName=admin-discord-mcp {#AppVersion}
WizardImageStretch=no
ShowLanguageDialog=no
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\bin\win-x64\admin-discord-mcp.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "install-task.ps1";                     DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall-task.ps1";                   DestDir: "{app}"; Flags: ignoreversion

[Run]
; Register + start the Scheduled Task (on-logon, auto-restart on crash).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\install-task.ps1"" -ExePath ""{app}\admin-discord-mcp.exe"""; \
  StatusMsg: "Registering background service..."; \
  Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\uninstall-task.ps1"""; \
  RunOnceId: "RemoveScheduledTask"; \
  Flags: runhidden

[UninstallDelete]
; Logs + audit trail in %LOCALAPPDATA%\admin-discord-mcp\ are kept on uninstall by
; design. Users who want a full wipe can delete that folder manually.

[Code]
var
  TokenPage: TInputQueryWizardPage;
  ClaudeRegistered: Boolean;
  ClaudeCliFound: Boolean;
  RegisteredWithToken: Boolean;

function CRLF(): String;
begin
  Result := Chr(13) + Chr(10);
end;

function GetEnteredToken(): String;
begin
  if TokenPage = nil then
    Result := ''
  else
    Result := Trim(TokenPage.Values[0]);
end;

function ClaudeCliAvailable(): Boolean;
var
  resultCode: Integer;
begin
  Result := Exec('cmd.exe', '/c where claude >nul 2>&1', '',
    SW_HIDE, ewWaitUntilTerminated, resultCode) and (resultCode = 0);
end;

// Idempotent: remove any prior user-scope registration, then add the HTTP URL.
// If a bot token was entered, attach it as the per-connection header so the MCP
// works out of the box; otherwise register URL-only and let the user add it.
function RegisterMcpInClaudeCode(): Boolean;
var
  resultCode: Integer;
  token, cmd: String;
begin
  Result := False;
  if not ClaudeCliAvailable() then Exit;

  Exec('cmd.exe',
    '/c claude mcp remove {#McpName} --scope user >nul 2>&1',
    '', SW_HIDE, ewWaitUntilTerminated, resultCode);

  token := GetEnteredToken();
  if token <> '' then
  begin
    cmd := '/c claude mcp add {#McpName} --scope user --transport http {#McpUrl}' +
           ' --header "{#TokenHeader}: ' + token + '" >nul 2>&1';
    RegisteredWithToken := True;
  end
  else
  begin
    cmd := '/c claude mcp add {#McpName} --scope user --transport http {#McpUrl} >nul 2>&1';
    RegisteredWithToken := False;
  end;

  if Exec('cmd.exe', cmd, '', SW_HIDE, ewWaitUntilTerminated, resultCode) then
    Result := (resultCode = 0);
end;

procedure InitializeWizard;
begin
  TokenPage := CreateInputQueryPage(wpSelectDir,
    'Discord Bot Token (optional)',
    'Paste the bot token the server should act as',
    'Get it from the Discord Developer Portal -> your app -> Bot -> Reset Token. ' +
    'It is NOT stored on the server; it is saved only into your Claude Code MCP ' +
    'registration as the "{#TokenHeader}" header. Leave blank to skip and add a ' +
    'token (or multiple bots) yourself later.');
  TokenPage.Add('Bot token (MTxxxx.yyyy.zzzz):', False);
  TokenPage.Values[0] := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ClaudeCliFound := ClaudeCliAvailable();
    if ClaudeCliFound then
      ClaudeRegistered := RegisterMcpInClaudeCode();
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  msg: String;
begin
  if CurPageID <> wpFinished then Exit;

  msg := 'admin-discord-mcp is installed.' + CRLF() + CRLF() +
         '  Server:  {#McpUrl}  (Scheduled Task: {#TaskName})' + CRLF();

  if ClaudeRegistered and RegisteredWithToken then
    msg := msg + '  Claude:  registered as user-scope MCP "{#McpName}" with your bot token.' + CRLF() + CRLF() +
           'Open any Claude Code session, run /mcp to confirm, then try:' + CRLF() +
           '    "List the roles in guild <id>."'
  else if ClaudeRegistered then
    msg := msg + '  Claude:  registered as "{#McpName}" (no token yet).' + CRLF() + CRLF() +
           'Add your bot token to the registration so calls can reach Discord:' + CRLF() +
           '    claude mcp remove {#McpName} --scope user' + CRLF() +
           '    claude mcp add {#McpName} --scope user --transport http {#McpUrl} --header "{#TokenHeader}: <token>"'
  else if not ClaudeCliFound then
    msg := msg + '  Claude:  CLI not found in PATH - register manually:' + CRLF() +
           '    claude mcp add {#McpName} --scope user --transport http {#McpUrl} --header "{#TokenHeader}: <token>"'
  else
    msg := msg + '  Claude:  registration failed - run manually:' + CRLF() +
           '    claude mcp add {#McpName} --scope user --transport http {#McpUrl} --header "{#TokenHeader}: <token>"';

  MsgBox(msg, mbInformation, MB_OK);
end;
