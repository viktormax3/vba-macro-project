Attribute VB_Name = "Módulo1"
#If VBA7 Then
  Declare PtrSafe Sub Sleep Lib "kernel32" (ByVal dwMilliseconds As Long)
#Else
  Declare Sub Sleep Lib "kernel32" (ByVal dwMilliseconds As Long)
#End If

