# UI Automation Gaps: uit-19-create-modes

This document records controls and functional aspects identified during the authoring of `CreateWorkspaceModesCoverageTests` that lack stable `AutomationProperties.AutomationId` values and are currently located by `ControlType` plus `Name` / `Text`.

## Missing AutomationIds

| Control | ControlType | Page / View | Current Selector | Suggested AutomationId |
|---|---|---|---|---|
| "Generate image" Button | `Button` | `CreateWorkspaceView.cs:147` | `cf.ByName("Generate image").And(cf.ByControlType(ControlType.Button))` | `Create_Image_Run_Button` |
| "Generate video" Button | `Button` | `CreateWorkspaceView.cs:183` | `cf.ByName("Generate video").And(cf.ByControlType(ControlType.Button))` | `Create_Video_Run_Button` |
| "Choose start image…" Button (Video) | `Button` | `CreateWorkspaceView.cs:166` | `cf.ByName("Choose start image…").And(cf.ByControlType(ControlType.Button))` | `Create_Video_ChooseStartImage_Button` |
| "Clear start image" Button | `Button` | `CreateWorkspaceView.cs:172` | `cf.ByName("Clear start image").And(cf.ByControlType(ControlType.Button))` | `Create_Video_ClearStartImage_Button` |
| Video Start Image Label | `Text` | `CreateWorkspaceView.cs:47, 196` | `cf.ByName("No start image: text-to-video").And(cf.ByControlType(ControlType.Text))` | `Create_Video_StartImage_Label_Text` |
| Video Start Image Aspect Check | `Text` | `CreateWorkspaceView.cs:48, 211` | `ControlType.Text` in Video panel | `Create_Video_StartImage_Check_Text` |
| Image Prompt TextBox | `Edit` | `CreateWorkspaceView.cs:35` | `ControlType.Edit` with placeholder "Describe the image to create" | `Create_Image_Prompt_TextBox` |
| Image Width NumberBox | `Spinner` / `Custom` | `CreateWorkspaceView.cs:36` | Labeled "Width" in Image panel | `Create_Image_Width_NumberBox` |
| Image Height NumberBox | `Spinner` / `Custom` | `CreateWorkspaceView.cs:37` | Labeled "Height" in Image panel | `Create_Image_Height_NumberBox` |
| Video Prompt TextBox | `Edit` | `CreateWorkspaceView.cs:39` | `ControlType.Edit` with placeholder "Describe the video to create" | `Create_Video_Prompt_TextBox` |
| Video Width NumberBox | `Spinner` / `Custom` | `CreateWorkspaceView.cs:40` | Labeled "Width" in Video panel | `Create_Video_Width_NumberBox` |
| Video Height NumberBox | `Spinner` / `Custom` | `CreateWorkspaceView.cs:41` | Labeled "Height" in Video panel | `Create_Video_Height_NumberBox` |
| Video Duration NumberBox | `Spinner` / `Custom` | `CreateWorkspaceView.cs:42` | Labeled "Seconds" in Video panel | `Create_Video_Duration_NumberBox` |
| Video FPS NumberBox | `Spinner` / `Custom` | `CreateWorkspaceView.cs:43` | Labeled "FPS" in Video panel | `Create_Video_Fps_NumberBox` |
| Video Steps NumberBox | `Spinner` / `Custom` | `CreateWorkspaceView.cs:44` | Labeled "Steps" in Video panel | `Create_Video_Steps_NumberBox` |
| Edit Choose Source Image Button | `Button` | `CreateWorkspaceView.cs:236` | `cf.ByName("Choose source image…").And(cf.ByControlType(ControlType.Button))` | `Create_Edit_ChooseSourceImage_Button` |
| Edit Source Image Label | `Text` | `CreateWorkspaceView.cs:51, 240` | `cf.ByName("No source image selected").And(cf.ByControlType(ControlType.Text))` | `Create_Edit_SourceImage_Label_Text` |
| Edit Prompt TextBox | `Edit` | `CreateWorkspaceView.cs:50` | `ControlType.Edit` with placeholder "Describe the edit" | `Create_Edit_Prompt_TextBox` |
| 3D Choose Source Image Button | `Button` | `CreateWorkspaceView.cs:280` | `cf.ByName("Choose source image…").And(cf.ByControlType(ControlType.Button))` | `Create_Threed_ChooseSourceImage_Button` |
| 3D Source Image Label | `Text` | `CreateWorkspaceView.cs:59, 284` | `cf.ByName("No source image selected").And(cf.ByControlType(ControlType.Text))` | `Create_Threed_SourceImage_Label_Text` |
| "Generate 3D model" Button | `Button` | `CreateWorkspaceView.cs:288` | `cf.ByName("Generate 3D model").And(cf.ByControlType(ControlType.Button))` | `Create_Threed_Run_Button` |
| STT Choose Audio File Button | `Button` | `CreateWorkspaceView.cs:315` | `cf.ByName("Choose audio file…").And(cf.ByControlType(ControlType.Button))` | `Create_STT_ChooseAudioFile_Button` |
| STT Audio File Label | `Text` | `CreateWorkspaceView.cs:66, 319` | `cf.ByName("No audio file selected").And(cf.ByControlType(ControlType.Text))` | `Create_STT_AudioFile_Label_Text` |
| "Transcribe" Button | `Button` | `CreateWorkspaceView.cs:323` | `cf.ByName("Transcribe").And(cf.ByControlType(ControlType.Button))` | `Create_STT_Run_Button` |
| STT Transcription TextBox | `Edit` | `CreateWorkspaceView.cs:67` | `ControlType.Edit` with placeholder "Transcription appears here" | `Create_STT_Transcription_TextBox` |
| Section Headings | `Text` | `CreateWorkspaceView.cs:130-135, 679` | `cf.ByName("IMAGE")`, `cf.ByName("VIDEO")`, etc. | `Create_<Mode>_Header_Text` |

## Functional and Architectural Notes

- **Avatar Mode**:
  - Avatar generation is not listed or embedded within `CreateWorkspaceView`.
  - Local Avatar Studio configuration and generation is currently managed under `SettingsView.xaml.cs` (Avatar Studio) rather than Create Workspace.
- **Model / Runtime Availability Feedback**:
  - In `CreateWorkspaceView`, generate buttons remain enabled initially and report dependency / missing engine errors on click or display guidance notes (e.g. 3D engine info text: "Uses the managed local 3D engine. If the engine is not installed, Runtime/Settings can install it before generation."), rather than disabling the generate button with an upfront disabled reason prior to model discovery.
