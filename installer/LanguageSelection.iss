function InvalidateLanguageRow(Window: HWND; Bounds: LongWord; Erase: Longint): Longint;
  external 'InvalidateRect@user32.dll stdcall';

procedure LanguageOptionChanged(Sender: TObject);
var
  Index: Integer;
begin
  for Index := 0 to 3 do
    if LanguageOptions[Index] <> nil then InvalidateLanguageRow(LanguageOptions[Index].Handle, 0, 0);
end;

function ChooseSetupLanguage: String;
var
  Form: TSetupForm;
  Mark: TBitmapImage;
  Prompt: TNewStaticText;
  Names, Codes: array[0..3] of String;
  Index, Selected, Margin, ContentLeft, ContentWidth, BodyHeight, OptionsTop: Integer;
  RowHeight, RowGap, HeadingHeight, MarkSize: Integer;
begin
  Result := '';
  Names[0] := 'Русский';
  Names[1] := 'English';
  Names[2] := 'Español';
  Names[3] := '简体中文';
  Codes[0] := 'russian';
  Codes[1] := 'english';
  Codes[2] := 'spanish';
  Codes[3] := 'chinesesimplified';
  Selected := 0;
  for Index := 0 to 3 do
    if ActiveLanguage = Codes[Index] then Selected := Index;
  ExtractTemporaryFile('icon.png');
  Form := CreateCustomForm(InstallerWidth, InstallerHeight, True, True);
  try
    Form.Caption := 'Di-Tunnel';
    SizeInstallerWindow(Form);
    Margin := ScaleX(InstallerMargin);
    ContentLeft := ScaleX(InstallerMarkColumn) + Margin;
    ContentWidth := Form.ClientWidth - ContentLeft - Margin;
    BodyHeight := Form.ClientHeight - ScaleY(InstallerFooterHeight);
    MarkSize := ScaleX(InstallerMarkSize);
    Mark := TBitmapImage.Create(Form);
    Mark.Parent := Form;
    LoadWizardMark(Mark, 'icon.png', MarkSize);
    Mark.Left := (ScaleX(InstallerMarkColumn) - MarkSize) div 2;
    Mark.Top := (BodyHeight - MarkSize) div 2;
    RowHeight := ScaleY(31);
    RowGap := ScaleY(5);
    HeadingHeight := ScaleY(27);
    OptionsTop := (BodyHeight - (HeadingHeight + 4 * RowHeight + 3 * RowGap)) div 2;
    Prompt := TNewStaticText.Create(Form);
    Prompt.Parent := Form;
    Prompt.StyleElements := [];
    Prompt.Font.Color := LightText;
    Prompt.Font.Size := 14;
    Prompt.Font.Style := [fsBold];
    Prompt.WordWrap := True;
    Prompt.AutoSize := False;
    Prompt.Caption := SetupMessage(msgSelectLanguageTitle);
    Prompt.SetBounds(ContentLeft, OptionsTop, ContentWidth, HeadingHeight);
    for Index := 0 to 3 do
    begin
      LanguageOptions[Index] := TNewRadioButton.Create(Form);
      LanguageOptions[Index].Parent := Form;
      LanguageOptions[Index].Caption := Names[Index];
      LanguageOptions[Index].SetBounds(ContentLeft, OptionsTop + HeadingHeight + (RowHeight + RowGap) * Index,
        ContentWidth, RowHeight);
      LanguageOptions[Index].OnClick := @LanguageOptionChanged;
      UseAccent(LanguageOptions[Index], DrawLanguageOptionFirst + Index);
    end;
    LanguageOptions[Selected].Checked := True;
    LanguageConfirm := TNewButton.Create(Form);
    LanguageConfirm.Parent := Form;
    LanguageConfirm.Caption := SetupMessage(msgButtonOK);
    LanguageConfirm.Default := True;
    LanguageConfirm.ModalResult := mrOK;
    UseAccent(LanguageConfirm, DrawLanguageConfirm);
    LanguageCancel := TNewButton.Create(Form);
    LanguageCancel.Parent := Form;
    LanguageCancel.Caption := SetupMessage(msgButtonCancel);
    LanguageCancel.Cancel := True;
    LanguageCancel.ModalResult := mrCancel;
    LayoutInstallerButtons(Form, LanguageConfirm, LanguageCancel, nil);
    UseAccent(LanguageCancel, DrawLanguageCancel);
    Form.ActiveControl := LanguageOptions[Selected];
    if Form.ShowModal = mrOK then
      for Index := 0 to 3 do
        if LanguageOptions[Index].Checked then Result := Codes[Index];
  finally
    Form.Free;
    for Index := 0 to 3 do LanguageOptions[Index] := nil;
    LanguageConfirm := nil;
    LanguageCancel := nil;
  end;
end;
