const
  InstallerWidth = 478;
  InstallerHeight = 268;
  InstallerMargin = 20;
  InstallerFooterHeight = 46;
  InstallerMarkColumn = 151;
  InstallerMarkSize = 142;
  WizardSurface = $1E1115;
  HeaderSurface = $2B1921;
  FooterSurface = $311F29;
  PaleViolet = $F5D9EE;
  Violet = $B45686;
  VioletHover = $D3679D;
  VioletPressed = $99466C;
  VioletEdge = $FFC7DC;
  LightText = $FFF6FB;
  DrawButton = 1;
  DrawTasks = 2;
  DrawFinishTasks = 3;
  DrawLanguageConfirm = 4;
  DrawExitYes = 5;
  DrawExitNo = 6;
  DrawCancel = 7;
  DrawLanguageCancel = 8;
  DrawLanguageOptionFirst = 10;
  WmPaint = $000F;
  WmNcDestroy = $0082;
  ButtonClickMessage = $00F5;
  SystemCloseCommand = $F060;

function SetWindowSubclass(Window: HWND; Callback: Longint; Id, Data: LongWord): Longint;
  external 'SetWindowSubclass@comctl32.dll stdcall';
function RemoveWindowSubclass(Window: HWND; Callback: Longint; Id: LongWord): Longint;
  external 'RemoveWindowSubclass@comctl32.dll stdcall';
function DefSubclassProc(Window: HWND; Msg: LongWord; WParam: LongWord; LParam: Longint): Longint;
  external 'DefSubclassProc@comctl32.dll stdcall';
function GetClientRect(Window: HWND; var Bounds: TRect): Longint;
  external 'GetClientRect@user32.dll stdcall';
function FillControlRect(DC: LongWord; var Bounds: TRect; Brush: LongWord): Longint;
  external 'FillRect@user32.dll stdcall';
function GetCursorPos(var Point: TPoint): Longint;
  external 'GetCursorPos@user32.dll stdcall';
function ScreenToClient(Window: HWND; var Point: TPoint): Longint;
  external 'ScreenToClient@user32.dll stdcall';
function GetFocus(): HWND;
  external 'GetFocus@user32.dll stdcall';
function GetDC(Window: HWND): LongWord;
  external 'GetDC@user32.dll stdcall';
function ReleaseDC(Window: HWND; DC: LongWord): Longint;
  external 'ReleaseDC@user32.dll stdcall';
function SendControlMessage(Window: HWND; Msg: LongWord; WParam: LongWord; LParam: Longint): Longint;
  external 'SendMessageW@user32.dll stdcall';
function PostControlMessage(Window: HWND; Msg: LongWord; WParam: LongWord; LParam: Longint): Longint;
  external 'PostMessageW@user32.dll stdcall';
function GetWizardSystemMenu(Window: HWND; Revert: Longint): LongWord;
  external 'GetSystemMenu@user32.dll stdcall';
function EnableWizardMenuItem(Menu, Item, Flags: LongWord): LongWord;
  external 'EnableMenuItem@user32.dll stdcall';
function RedrawWizardMenuBar(Window: HWND): Longint;
  external 'DrawMenuBar@user32.dll stdcall';
function ReadListItemRect(Window: HWND; Msg: LongWord; Index: LongWord; var Bounds: TRect): Longint;
  external 'SendMessageW@user32.dll stdcall';
function DrawButtonText(DC: LongWord; Text: String; Length: Integer; var Bounds: TRect; Format: LongWord): Integer;
  external 'DrawTextW@user32.dll stdcall';
function SetTextColor(DC: LongWord; Color: LongWord): LongWord;
  external 'SetTextColor@gdi32.dll stdcall';
function SetBkMode(DC: LongWord; Mode: Integer): Integer;
  external 'SetBkMode@gdi32.dll stdcall';
function SelectGdiObject(DC: LongWord; Handle: LongWord): LongWord;
  external 'SelectObject@gdi32.dll stdcall';
function DeleteGdiObject(Handle: LongWord): Longint;
  external 'DeleteObject@gdi32.dll stdcall';
function CreateSolidBrush(Color: LongWord): LongWord;
  external 'CreateSolidBrush@gdi32.dll stdcall';
function CreatePen(Style, Width: Integer; Color: LongWord): LongWord;
  external 'CreatePen@gdi32.dll stdcall';
function DrawRoundedRect(DC: LongWord; Left, Top, Right, Bottom, Width, Height: Integer): Longint;
  external 'RoundRect@gdi32.dll stdcall';
function MovePen(DC: LongWord; X, Y: Integer; Previous: LongWord): Longint;
  external 'MoveToEx@gdi32.dll stdcall';
function DrawLine(DC: LongWord; X, Y: Integer): Longint;
  external 'LineTo@gdi32.dll stdcall';
function LoadStockBitmap(Instance, Id: LongWord): LongWord;
  external 'LoadBitmapW@user32.dll stdcall';

var
  AppearanceCallback: Longint;
  CheckWidth, CheckHeight: Integer;
  AppHero: TPanel;
  LanguageConfirm, LanguageCancel: TNewButton;
  LanguageOptions: array[0..3] of TNewRadioButton;
  ExitYes, ExitNo: TNewButton;

procedure DrawAccent(DC: LongWord; Bounds: TRect; Fill, Border: LongWord; Radius: Integer);
var
  Brush, Pen, OldBrush, OldPen: LongWord;
begin
  Brush := CreateSolidBrush(Fill);
  Pen := CreatePen(0, 1, Border);
  OldBrush := SelectGdiObject(DC, Brush);
  OldPen := SelectGdiObject(DC, Pen);
  DrawRoundedRect(DC, Bounds.Left, Bounds.Top, Bounds.Right, Bounds.Bottom, Radius, Radius);
  SelectGdiObject(DC, OldPen);
  SelectGdiObject(DC, OldBrush);
  DeleteGdiObject(Pen);
  DeleteGdiObject(Brush);
end;

procedure PaintWizardSurface(DC: LongWord; var Bounds: TRect);
var
  Background: LongWord;
begin
  Background := CreateSolidBrush(WizardSurface);
  FillControlRect(DC, Bounds, Background);
  DeleteGdiObject(Background);
end;

procedure PaintAccentButton(Window: HWND; Button: TNewButton; NormalColor: LongWord);
var
  Bounds: TRect;
  Cursor: TPoint;
  DC: LongWord;
  Fill, Border: LongWord;
  PreviousFont: LongWord;
begin
  if not Button.Enabled then Exit;
  if GetClientRect(Window, Bounds) = 0 then Exit;
  DC := GetDC(Window);
  if DC = 0 then Exit;
  Fill := NormalColor;
  if (SendControlMessage(Window, $00F2, 0, 0) and 4) <> 0 then Fill := VioletPressed
  else if (GetCursorPos(Cursor) <> 0) and (ScreenToClient(Window, Cursor) <> 0)
    and (Cursor.X >= 0) and (Cursor.X < Bounds.Right)
    and (Cursor.Y >= 0) and (Cursor.Y < Bounds.Bottom) then Fill := VioletHover;
  Border := Fill;
  if GetFocus() = Window then Border := VioletEdge;
  PaintWizardSurface(DC, Bounds);
  DrawAccent(DC, Bounds, Fill, Border, ScaleX(8));
  PreviousFont := SelectGdiObject(DC, Button.Font.Handle);
  SetBkMode(DC, 1);
  SetTextColor(DC, LightText);
  DrawButtonText(DC, Button.Caption, -1, Bounds, $25);
  SelectGdiObject(DC, PreviousFont);
  ReleaseDC(Window, DC);
end;

procedure PaintLanguageOption(Window: HWND; Option: TNewRadioButton);
var
  Bounds, Mark: TRect;
  DC, PreviousFont, Fill, Border: LongWord;
  DotSize: Integer;
begin
  if GetClientRect(Window, Bounds) = 0 then Exit;
  DC := GetDC(Window);
  if DC = 0 then Exit;
  PaintWizardSurface(DC, Bounds);
  Fill := HeaderSurface;
  Border := FooterSurface;
  if Option.Checked then
  begin
    Fill := VioletPressed;
    Border := Violet;
  end;
  if GetFocus() = Window then Border := VioletEdge;
  DrawAccent(DC, Bounds, Fill, Border, ScaleX(10));
  DotSize := ScaleX(12);
  Mark.Left := ScaleX(12);
  Mark.Top := (Bounds.Bottom - DotSize) div 2;
  Mark.Right := Mark.Left + DotSize;
  Mark.Bottom := Mark.Top + DotSize;
  if Option.Checked then Fill := LightText;
  DrawAccent(DC, Mark, Fill, PaleViolet, DotSize);
  Bounds.Left := ScaleX(34);
  PreviousFont := SelectGdiObject(DC, Option.Font.Handle);
  SetBkMode(DC, 1);
  SetTextColor(DC, LightText);
  DrawButtonText(DC, Option.Caption, -1, Bounds, $824);
  SelectGdiObject(DC, PreviousFont);
  ReleaseDC(Window, DC);
end;

procedure PaintCheckBoxes(Window: HWND; List: TNewCheckListBox);
var
  Index, X, Y, Indent: Integer;
  Cell, Glyph: TRect;
  DC: LongWord;
  Fill, Border, Pen, PreviousPen: LongWord;
  Checked, Enabled: Boolean;
begin
  if (CheckWidth <= 0) or (CheckHeight <= 0) then Exit;
  DC := GetDC(Window);
  if DC = 0 then Exit;
  Indent := CheckWidth + 2 * List.Offset;
  for Index := 0 to List.Items.Count - 1 do
    if ReadListItemRect(Window, $0198, Index, Cell) <> -1 then
    begin
      { The styled Windows glyph can extend outside the stock bitmap bounds.
        Clear the whole check cell up to the caption before drawing either state. }
      Cell.Left := Cell.Left + Indent * List.ItemLevel[Index];
      Cell.Right := Cell.Left + Indent;
      PaintWizardSurface(DC, Cell);
      X := Cell.Left + List.Offset;
      Y := Cell.Top + (Cell.Bottom - Cell.Top - CheckHeight) div 2;
      Glyph.Left := X;
      Glyph.Top := Y;
      Glyph.Right := X + CheckWidth;
      Glyph.Bottom := Y + CheckHeight;
      Enabled := List.Enabled and List.ItemEnabled[Index];
      Checked := List.Checked[Index];
      Fill := WizardSurface;
      Border := PaleViolet;
      if Checked then
      begin
        if Enabled then Fill := Violet else Fill := FooterSurface;
        Border := Fill;
      end
      else if not Enabled then Border := FooterSurface;
      DrawAccent(DC, Glyph, Fill, Border, ScaleX(4));
      if Checked then
      begin
        Pen := CreatePen(0, 2, LightText);
        PreviousPen := SelectGdiObject(DC, Pen);
        MovePen(DC, X + CheckWidth div 4, Y + CheckHeight div 2, 0);
        DrawLine(DC, X + CheckWidth * 2 div 5, Y + CheckHeight * 7 div 10);
        DrawLine(DC, X + CheckWidth * 4 div 5, Y + CheckHeight div 4);
        SelectGdiObject(DC, PreviousPen);
        DeleteGdiObject(Pen);
      end;
    end;
  ReleaseDC(Window, DC);
end;

function PaintWizardControl(Window: HWND; Msg: LongWord; WParam: LongWord;
  LParam: Longint; Id, Data: LongWord): Longint;
begin
  if Msg = WmNcDestroy then RemoveWindowSubclass(Window, AppearanceCallback, Id);
  Result := DefSubclassProc(Window, Msg, WParam, LParam);
  { Keep Inno's native input, focus, captions and task selection. }
  if Msg = WmPaint then
    case Id of
      DrawButton: PaintAccentButton(Window, WizardForm.NextButton, Violet);
      DrawTasks: PaintCheckBoxes(Window, WizardForm.TasksList);
      DrawFinishTasks: PaintCheckBoxes(Window, WizardForm.RunList);
      DrawLanguageConfirm: PaintAccentButton(Window, LanguageConfirm, Violet);
      DrawExitYes: PaintAccentButton(Window, ExitYes, VioletPressed);
      DrawExitNo: PaintAccentButton(Window, ExitNo, Violet);
      DrawCancel: PaintAccentButton(Window, WizardForm.CancelButton, VioletPressed);
      DrawLanguageCancel: PaintAccentButton(Window, LanguageCancel, VioletPressed);
      DrawLanguageOptionFirst..DrawLanguageOptionFirst + 3:
        PaintLanguageOption(Window, LanguageOptions[Id - DrawLanguageOptionFirst]);
    end;
end;

procedure UseAccent(Control: TWinControl; Kind: LongWord);
begin
  if AppearanceCallback = 0 then AppearanceCallback := CreateCallback(@PaintWizardControl);
  SetWindowSubclass(Control.Handle, AppearanceCallback, Kind, 0);
end;

procedure LoadWizardMark(Image: TBitmapImage; FileName: String; Size: Integer);
begin
  Image.PngImage.LoadFromFile(ExpandConstant('{tmp}\') + FileName);
  Image.AutoSize := False;
  Image.Width := Size;
  Image.Height := Size;
  Image.BackColor := clNone;
  Image.Stretch := True;
  Image.Visible := True;
end;

procedure LayoutInstallerButtons(Form: TSetupForm; Primary, Cancel, Back: TNewButton);
var
  Width, Height, Gap, Top: Integer;
begin
  Width := ScaleX(86);
  Height := ScaleY(28);
  Gap := ScaleX(8);
  Top := Form.ClientHeight - (ScaleY(InstallerFooterHeight) + Height) div 2;
  Cancel.SetBounds(Form.ClientWidth - ScaleX(InstallerMargin) - Width, Top, Width, Height);
  Primary.SetBounds(Cancel.Left - Gap - Width, Top, Width, Height);
  if Back <> nil then Back.SetBounds(Primary.Left - Gap - Width, Top, Width, Height);
end;

procedure SizeInstallerWindow(Form: TSetupForm);
begin
  Form.BorderStyle := bsSingle;
  Form.BorderIcons := [biSystemMenu, biMinimize];
  Form.StyleElements := [seBorder];
  Form.Color := WizardSurface;
  Form.ClientWidth := ScaleX(InstallerWidth);
  Form.ClientHeight := ScaleY(InstallerHeight);
  Form.CenterOnShow := True;
end;

procedure InitializeWizard;
var
  Footer: TPanel;
  AppMark, PublisherMark: TBitmapImage;
  Stock: TBitmap;
  HeroWidth, IconSize, FooterTop, Margin, ContentLeft, ContentWidth: Integer;
  PublisherSize, WelcomeWidth, WelcomeHeight, HeaderEnd, HintBreak: Integer;
begin
  if WizardSilent then Exit;
  SizeInstallerWindow(WizardForm);
  LayoutInstallerButtons(WizardForm, WizardForm.NextButton, WizardForm.CancelButton, WizardForm.BackButton);
  ExtractTemporaryFile('icon.png');
  ExtractTemporaryFile('di-vinty-interactive.png');
  FooterTop := WizardForm.ClientHeight - ScaleY(InstallerFooterHeight);
  WizardForm.OuterNotebook.Height := FooterTop;
  HeroWidth := ScaleX(InstallerMarkColumn);
  IconSize := ScaleX(InstallerMarkSize);
  Margin := ScaleX(InstallerMargin);
  ContentLeft := HeroWidth + Margin;
  ContentWidth := WizardForm.OuterNotebook.Width - ContentLeft - Margin;
  WizardForm.MainPanel.BevelOuter := bvNone;
  WizardForm.MainPanel.StyleElements := [];
  WizardForm.MainPanel.Color := WizardSurface;
  WizardForm.PageNameLabel.StyleElements := [];
  WizardForm.PageNameLabel.Color := WizardSurface;
  WizardForm.PageNameLabel.Font.Color := LightText;
  WizardForm.PageNameLabel.Font.Size := 14;
  WizardForm.PageNameLabel.AutoSize := False;
  WizardForm.PageNameLabel.SetBounds(ContentLeft, ScaleY(16), ContentWidth, ScaleY(25));
  WizardForm.PageDescriptionLabel.StyleElements := [];
  WizardForm.PageDescriptionLabel.Color := WizardSurface;
  WizardForm.PageDescriptionLabel.Font.Color := PaleViolet;
  WizardForm.PageDescriptionLabel.SetBounds(ContentLeft, ScaleY(43), ContentWidth, ScaleY(34));
  HeaderEnd := WizardForm.PageDescriptionLabel.Top + WizardForm.PageDescriptionLabel.Height;
  WizardForm.MainPanel.Height := HeaderEnd;
  WizardForm.InnerNotebook.SetBounds(ContentLeft, HeaderEnd, ContentWidth, FooterTop - HeaderEnd);
  WizardForm.SelectTasksLabel.Visible := False;
  WizardForm.TasksList.Anchors := [akLeft, akTop, akRight];
  WizardForm.TasksList.MinItemHeight := ScaleY(32);
  WizardForm.SelectDirBitmapImage.Visible := False;
  WizardForm.SelectDirLabel.Visible := False;
  WizardForm.SelectDirBrowseLabel.Visible := False;
  WizardForm.WizardSmallBitmapImage.Visible := False;
  WizardForm.WizardBitmapImage.Visible := False;
  WizardForm.WizardBitmapImage2.Visible := False;
  WizardForm.FinishedHeadingLabel.Left := ContentLeft;
  WizardForm.FinishedLabel.Left := ContentLeft;
  WizardForm.RunList.Left := ContentLeft;
  WizardForm.YesRadio.Left := ContentLeft;
  WizardForm.NoRadio.Left := ContentLeft;
  WizardForm.FinishedHeadingLabel.Width := ContentWidth;
  WizardForm.FinishedLabel.Width := ContentWidth;
  WizardForm.RunList.Width := ContentWidth;

  PublisherSize := ScaleX(154);
  PublisherMark := TBitmapImage.Create(WizardForm.WelcomePage);
  PublisherMark.Parent := WizardForm.WelcomePage;
  LoadWizardMark(PublisherMark, 'di-vinty-interactive.png', PublisherSize);
  WelcomeWidth := WizardForm.OuterNotebook.Width - 2 * Margin;
  WizardForm.WelcomeLabel1.Left := Margin;
  WizardForm.WelcomeLabel1.Width := WelcomeWidth;
  WizardForm.WelcomeLabel1.Alignment := taCenter;
  WizardForm.WelcomeLabel1.Caption := Trim(WizardForm.WelcomeLabel1.Caption);
  WizardForm.WelcomeLabel1.Height := ScaleY(24);
  WizardForm.WelcomeLabel2.Left := Margin;
  WizardForm.WelcomeLabel2.Width := WelcomeWidth;
  WizardForm.WelcomeLabel2.Alignment := taCenter;
  { Inno appends its navigation instructions as a second paragraph. }
  HintBreak := Pos(#13#10#13#10, WizardForm.WelcomeLabel2.Caption);
  if HintBreak > 0 then
    WizardForm.WelcomeLabel2.Caption := Copy(WizardForm.WelcomeLabel2.Caption, 1, HintBreak - 1);
  WizardForm.WelcomeLabel2.Height := ScaleY(18);
  WizardForm.AdjustLabelHeight(WizardForm.WelcomeLabel1);
  WizardForm.AdjustLabelHeight(WizardForm.WelcomeLabel2);
  WelcomeHeight := PublisherSize + WizardForm.WelcomeLabel1.Height + WizardForm.WelcomeLabel2.Height + ScaleY(16);
  PublisherMark.Left := (WizardForm.OuterNotebook.Width - PublisherSize) div 2;
  PublisherMark.Top := (WizardForm.OuterNotebook.Height - WelcomeHeight) div 2;
  WizardForm.WelcomeLabel1.Top := PublisherMark.Top + PublisherSize + ScaleY(10);
  WizardForm.WelcomeLabel2.Top := WizardForm.WelcomeLabel1.Top + WizardForm.WelcomeLabel1.Height + ScaleY(6);

  AppHero := TPanel.Create(WizardForm);
  AppHero.Parent := WizardForm;
  AppHero.StyleElements := [];
  AppHero.Color := WizardSurface;
  AppHero.BevelOuter := bvNone;
  AppHero.SetBounds(0, 0, HeroWidth, FooterTop);
  AppHero.Anchors := [akLeft, akTop, akBottom];
  AppHero.Visible := False;
  AppMark := TBitmapImage.Create(AppHero);
  AppMark.Parent := AppHero;
  LoadWizardMark(AppMark, 'icon.png', IconSize);
  AppMark.Left := (HeroWidth - IconSize) div 2;
  AppMark.Top := (FooterTop - IconSize) div 2;

  Footer := TPanel.Create(WizardForm);
  Footer.Parent := WizardForm;
  Footer.StyleElements := [];
  Footer.Color := WizardSurface;
  Footer.BevelOuter := bvNone;
  Footer.SetBounds(0, FooterTop, WizardForm.ClientWidth, WizardForm.ClientHeight - FooterTop);
  Footer.Anchors := [akLeft, akRight, akBottom];
  Footer.SendToBack;
  WizardForm.Bevel.Visible := False;
  WizardForm.Bevel1.Visible := False;
  WizardForm.BeveledLabel.Visible := False;

  Stock := TBitmap.Create;
  try
    Stock.Handle := LoadStockBitmap(0, 32759);
    CheckWidth := Stock.Width div 4;
    CheckHeight := Stock.Height div 3;
  finally
    Stock.Free;
  end;
  UseAccent(WizardForm.NextButton, DrawButton);
  UseAccent(WizardForm.CancelButton, DrawCancel);
  UseAccent(WizardForm.TasksList, DrawTasks);
  UseAccent(WizardForm.RunList, DrawFinishTasks);
end;

procedure FinishFromTitleBar(Sender: TObject; var Action: TCloseAction);
begin
  Action := caNone;
  PostControlMessage(WizardForm.NextButton.Handle, ButtonClickMessage, 0, 0);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Center, HeadingTop, RowHeight: Integer;
begin
  if WizardSilent then Exit;
  AppHero.Visible := CurPageID <> wpWelcome;
  WizardForm.MainPanel.Visible := CurPageID <> wpSelectTasks;
  WizardForm.PageDescriptionLabel.Visible := CurPageID <> wpSelectDir;
  if CurPageID = wpSelectDir then
  begin
    WizardForm.DirEdit.Top := ScaleY(8);
    WizardForm.DirBrowseButton.Top := WizardForm.DirEdit.Top + (WizardForm.DirEdit.Height - WizardForm.DirBrowseButton.Height) div 2;
    WizardForm.DirBrowseButton.Left := WizardForm.SelectDirPage.ClientWidth - WizardForm.DirBrowseButton.Width;
    WizardForm.DirEdit.Width := WizardForm.DirBrowseButton.Left - WizardForm.DirEdit.Left - ScaleX(8);
    WizardForm.DiskSpaceLabel.WordWrap := True;
    WizardForm.DiskSpaceLabel.SetBounds(0, WizardForm.DirEdit.Top + WizardForm.DirEdit.Height + ScaleY(10),
      WizardForm.SelectDirPage.ClientWidth, ScaleY(34));
  end;
  if CurPageID = wpSelectTasks then
  begin
    RowHeight := ScaleY(32);
    Center := WizardForm.OuterNotebook.Height div 2 - WizardForm.InnerNotebook.Top;
    WizardForm.TasksList.SetBounds(0, Center - RowHeight div 2, WizardForm.SelectTasksPage.ClientWidth, RowHeight + ScaleY(4));
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
  end;
  if CurPageID = wpFinished then
  begin
    { Inno disables the system Close command when its Cancel button is hidden. }
    WizardForm.OnClose := @FinishFromTitleBar;
    EnableWizardMenuItem(GetWizardSystemMenu(WizardForm.Handle, 0), SystemCloseCommand, 0);
    RedrawWizardMenuBar(WizardForm.Handle);
  end;
  if (CurPageID = wpFinished) and not (WizardForm.YesRadio.Visible or WizardForm.NoRadio.Visible) then
  begin
    WizardForm.FinishedLabel.Visible := False;
    WizardForm.FinishedLabel.Caption := '';
    WizardForm.AdjustLabelHeight(WizardForm.FinishedHeadingLabel);
    Center := WizardForm.OuterNotebook.Height div 2;
    HeadingTop := Center - WizardForm.FinishedHeadingLabel.Height div 2;
    if WizardForm.RunList.Visible then
    begin
      RowHeight := ScaleY(32);
      HeadingTop := HeadingTop - (RowHeight + ScaleY(8)) div 2;
      WizardForm.RunList.MinItemHeight := RowHeight;
      WizardForm.RunList.Top := HeadingTop + WizardForm.FinishedHeadingLabel.Height + ScaleY(8);
      WizardForm.RunList.Height := RowHeight + ScaleY(4);
    end;
    WizardForm.FinishedHeadingLabel.Top := HeadingTop;
  end;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
var
  Form: TSetupForm;
  Prompt: TNewStaticText;
  Margin, ButtonWidth, ButtonTop: Integer;
begin
  if WizardSilent or not Cancel or not Confirm then Exit;
  Form := CreateCustomForm(385, 170, True, True);
  try
    Form.Caption := SetupMessage(msgExitSetupTitle);
    Form.BorderStyle := bsDialog;
    Form.StyleElements := [];
    Form.Color := WizardSurface;
    Margin := ScaleX(20);
    Prompt := TNewStaticText.Create(Form);
    Prompt.Parent := Form;
    Prompt.StyleElements := [];
    Prompt.Font.Color := LightText;
    Prompt.AutoSize := False;
    Prompt.WordWrap := True;
    Prompt.Caption := SetupMessage(msgExitSetupMessage);
    Prompt.SetBounds(Margin, ScaleY(16), Form.ClientWidth - 2 * Margin, ScaleY(80));
    WizardForm.AdjustLabelHeight(Prompt);
    ButtonTop := Prompt.Top + Prompt.Height + ScaleY(18);
    ButtonWidth := ScaleX(84);
    Form.ClientHeight := ButtonTop + ScaleY(42);
    ExitNo := TNewButton.Create(Form);
    ExitNo.Parent := Form;
    ExitNo.Caption := SetupMessage(msgButtonNo);
    ExitNo.SetBounds(Form.ClientWidth - Margin - ButtonWidth, ButtonTop, ButtonWidth, ScaleY(28));
    ExitNo.ModalResult := mrNo;
    ExitNo.Default := True;
    ExitNo.Cancel := True;
    UseAccent(ExitNo, DrawExitNo);
    ExitYes := TNewButton.Create(Form);
    ExitYes.Parent := Form;
    ExitYes.Caption := SetupMessage(msgButtonYes);
    ExitYes.SetBounds(ExitNo.Left - ScaleX(8) - ButtonWidth, ButtonTop, ButtonWidth, ExitNo.Height);
    ExitYes.ModalResult := mrYes;
    UseAccent(ExitYes, DrawExitYes);
    Form.ActiveControl := ExitNo;
    Form.FlipAndCenterIfNeeded(True, WizardForm, True);
    Cancel := Form.ShowModal = mrYes;
    Confirm := False;
  finally
    Form.Free;
    ExitNo := nil;
    ExitYes := nil;
  end;
end;
