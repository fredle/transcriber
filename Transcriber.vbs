' Double-click launcher for the Meeting Transcriber GUI.
' Runs launcher.py with the project's venv pythonw.exe so no console
' window appears. Paths are resolved relative to this file, so the whole
' project folder can be moved or renamed without editing anything.

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

base = fso.GetParentFolderName(WScript.ScriptFullName)
pythonw = base & "\.venv\Scripts\pythonw.exe"
script = base & "\launcher.py"

If Not fso.FileExists(pythonw) Then
    MsgBox "Could not find the virtual environment:" & vbCrLf & vbCrLf & _
           pythonw & vbCrLf & vbCrLf & _
           "Create it and install requirements.txt first.", _
           16, "Meeting Transcriber"
    WScript.Quit 1
End If

If Not fso.FileExists(script) Then
    MsgBox "Could not find launcher.py:" & vbCrLf & vbCrLf & script, _
           16, "Meeting Transcriber"
    WScript.Quit 1
End If

shell.CurrentDirectory = base
shell.Run """" & pythonw & """ """ & script & """", 0, False
