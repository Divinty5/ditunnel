; Inno Setup 6.7 or later.
[Setup]
WizardStyle=modern dark includetitlebar hidebevels
WizardSizePercent=100,100
WizardBackColor=#15111E
DisableWelcomePage=no
DisableReadyPage=yes
WizardImageFile=
WizardSmallImageFile=
SetupIconFile=..\src\DiTunnel.Desktop\app.ico

[LangOptions]
DialogFontSize=11
WelcomeFontSize=16

[Messages]
russian.WelcomeLabel1=Мастер установки [name]
russian.WelcomeLabel2=Программа установит [name/ver] на ваш компьютер.
russian.FinishedHeadingLabel=Установка завершена
english.WelcomeLabel1=[name] Setup Wizard
english.WelcomeLabel2=This will install [name/ver] on your computer.
english.FinishedHeadingLabel=Installation complete
spanish.WelcomeLabel1=Asistente de instalación de [name]
spanish.WelcomeLabel2=Este programa instalará [name/ver] en su sistema.
spanish.FinishedHeadingLabel=Instalación completada
chinesesimplified.WelcomeLabel1=[name] 安装向导
chinesesimplified.WelcomeLabel2=即将在您的计算机上安装 [name/ver]。
chinesesimplified.FinishedHeadingLabel=安装完成

[Files]
Source: "..\icon.png"; Flags: dontcopy
Source: "Assets\di-vinty-interactive.png"; Flags: dontcopy

[Code]
#include "WizardAppearance.iss"
#include "LanguageSelection.iss"
