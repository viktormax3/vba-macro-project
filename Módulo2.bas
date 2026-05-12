Attribute VB_Name = "Módulo2"
Public Sub ForzarRectangulosSeleccionados()
    Dim s As Shape
    Dim nuevo As Shape
    Dim hechos As Long
    
    If ActiveSelectionRange.Count = 0 Then
        MsgBox "No hay objetos seleccionados."
        Exit Sub
    End If
    
    ActiveDocument.BeginCommandGroup "Forzar rectángulos"
    
    For Each s In ActiveSelectionRange
        If s.Type = cdrCurveShape Then
            Set nuevo = ReemplazarCurvaPorRectanguloExacto(s)
            If Not nuevo Is Nothing Then hechos = hechos + 1
        End If
    Next s
    
    ActiveDocument.EndCommandGroup
    
    MsgBox "Rectángulos creados: " & hechos
End Sub

Public Sub ForzarElipsesSeleccionadas()
    Dim s As Shape
    Dim nuevo As Shape
    Dim hechos As Long
    
    If ActiveSelectionRange.Count = 0 Then
        MsgBox "No hay objetos seleccionados."
        Exit Sub
    End If
    
    ActiveDocument.BeginCommandGroup "Forzar elipses"
    
    For Each s In ActiveSelectionRange
        If s.Type = cdrCurveShape Then
            Set nuevo = ReemplazarCurvaPorElipseExacta(s)
            If Not nuevo Is Nothing Then hechos = hechos + 1
        End If
    Next s
    
    ActiveDocument.EndCommandGroup
    
    MsgBox "Elipses creadas: " & hechos
End Sub

Private Function ReemplazarCurvaPorRectanguloExacto(ByVal s As Shape) As Shape
    Dim nuevo As Shape
    Dim l As Double, t As Double, r As Double, b As Double
    Dim rot As Double
    Dim ordenDelante As Boolean
    
    On Error GoTo Salida
    
    l = s.LeftX
    t = s.TopY
    r = s.RightX
    b = s.BottomY
    rot = s.RotationAngle
    
    ' CreateRectangle usa upper-left y lower-right
    Set nuevo = ActiveLayer.CreateRectangle(l, t, r, b)
    
    CopiarEstiloBasicoSeguro s, nuevo
    
    ' Mantener rotación si existía
    If rot <> 0 Then
        nuevo.RotationAngle = rot
        nuevo.SetPositionEx cdrCenter, s.CenterX, s.CenterY
    End If
    
    s.Delete
    Set ReemplazarCurvaPorRectanguloExacto = nuevo
    Exit Function
    
Salida:
    Set ReemplazarCurvaPorRectanguloExacto = Nothing
End Function

Private Function ReemplazarCurvaPorElipseExacta(ByVal s As Shape) As Shape
    Dim nuevo As Shape
    Dim cx As Double, cy As Double
    Dim rx As Double, ry As Double
    Dim rot As Double
    
    On Error GoTo Salida
    
    cx = s.CenterX
    cy = s.CenterY
    rx = s.SizeWidth / 2
    ry = s.SizeHeight / 2
    rot = s.RotationAngle
    
    ' CreateEllipse2 usa centro + radios
    Set nuevo = ActiveLayer.CreateEllipse2(cx, cy, rx, ry)
    
    CopiarEstiloBasicoSeguro s, nuevo
    
    If rot <> 0 Then nuevo.RotationAngle = rot
    
    ' Forzar centro exacto con reference point explícito
    nuevo.SetPositionEx cdrCenter, cx, cy
    
    s.Delete
    Set ReemplazarCurvaPorElipseExacta = nuevo
    Exit Function
    
Salida:
    Set ReemplazarCurvaPorElipseExacta = Nothing
End Function

Private Sub CopiarEstiloBasicoSeguro(ByVal origen As Shape, ByVal destino As Shape)
    On Error Resume Next
    
    destino.Name = origen.Name
    
    ' Fill
    Select Case origen.Fill.Type
        Case cdrUniformFill
            destino.Fill.ApplyUniformFill origen.Fill.UniformColor
        Case cdrNoFill
            destino.Fill.ApplyNoFill
        Case Else
            ' fallback simple
            destino.Fill.ApplyNoFill
    End Select
    
    ' Outline
    destino.Outline.SetPropertiesEx _
        origen.Outline.Width, _
        origen.Outline.Style, _
        origen.Outline.Color, _
        origen.Outline.StartArrow, _
        origen.Outline.EndArrow, _
        origen.Outline.Justification, _
        origen.Outline.Caps, _
        origen.Outline.CornerType, _
        origen.Outline.PenWidth, _
        origen.Outline.ScaleWithShape, _
        origen.Outline.DashDotLength, _
        origen.Outline.DashLength, _
        origen.Outline.DotLength
    
    On Error GoTo 0
End Sub

Public Sub IgualarAlturaSeleccionados()
    ' ================= CONFIG =================
    Dim alturaObjetivo As Double
    Dim usarAlturaMayor As Boolean
    Dim usarAlturaPromedio As Boolean
    Dim mantenerProporcion As Boolean
    
    usarAlturaMayor = False
    usarAlturaPromedio = False
    mantenerProporcion = True
    
    alturaObjetivo = 5 ' centímetros
    
    ' ==========================================
    
    Dim sr As ShapeRange
    Dim s As Shape
    Dim maxH As Double
    Dim sumaH As Double
    
    Dim unidadOriginal As cdrUnit
    
    If ActiveSelectionRange.Count = 0 Then
        MsgBox "No hay objetos seleccionados."
        Exit Sub
    End If
    
    ' ?? Guardar unidad actual
    unidadOriginal = ActiveDocument.Unit
    
    ' ?? Forzar centímetros
    ActiveDocument.Unit = cdrCentimeter
    
    Set sr = ActiveSelectionRange
    
    ' --- Determinar altura objetivo ---
    If usarAlturaMayor Then
        For Each s In sr
            If s.SizeHeight > maxH Then maxH = s.SizeHeight
        Next s
        alturaObjetivo = maxH
        
    ElseIf usarAlturaPromedio Then
        For Each s In sr
            sumaH = sumaH + s.SizeHeight
        Next s
        alturaObjetivo = sumaH / sr.Count
    End If
    
    ' --- Aplicar cambios ---
    ActiveDocument.BeginCommandGroup "Igualar altura (cm)"
    
    For Each s In sr
        If mantenerProporcion Then
            s.SetSize 0, alturaObjetivo
        Else
            s.SizeHeight = alturaObjetivo
        End If
    Next s
    
    ActiveDocument.EndCommandGroup
    
    ' ?? Restaurar unidad original
    ActiveDocument.Unit = unidadOriginal
    
    MsgBox "Altura aplicada: " & Format(alturaObjetivo, "0.00") & " cm"
End Sub
Public Sub ConservarSoloObjetosConBorde()
    Dim sr As ShapeRange
    Dim s As Shape
    Dim i As Long
    Dim tieneBorde As Boolean
    
    If ActiveSelectionRange.Count = 0 Then
        MsgBox "No hay objetos seleccionados."
        Exit Sub
    End If
    
    Set sr = ActiveSelectionRange
    
    ActiveDocument.BeginCommandGroup "Conservar solo objetos con borde"
    
    For i = sr.Count To 1 Step -1
        Set s = sr(i)
        tieneBorde = False
        
        On Error Resume Next
        If s.Outline.Type <> cdrNoOutline Then
            If s.Outline.Width > 0 Then
                tieneBorde = True
            End If
        End If
        On Error GoTo 0
        
        If Not tieneBorde Then
            s.Delete
        End If
    Next i
    
    ActiveDocument.EndCommandGroup
    
    MsgBox "Proceso completado."
End Sub
