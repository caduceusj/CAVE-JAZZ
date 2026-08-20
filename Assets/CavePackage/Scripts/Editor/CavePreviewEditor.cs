using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace CaveJazz.Calibration.EditorTools
{
    /// <summary>
    /// Rotulos do preview no Scene View: nome da face, tamanho em unidades, proporcao da
    /// Render Texture e o desvio de cobertura medido.
    /// </summary>
    [CustomEditor(typeof(CavePreview))]
    public class CavePreviewEditor : Editor
    {
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        private static GUIStyle labelStyle;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            CavePreview preview = (CavePreview)target;
            CaveRig rig = preview.Rig;

            if (rig == null)
            {
                return;
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Proporcoes e cobertura", EditorStyles.boldLabel);
            CaveCalibrationGUI.DrawFaceTable(rig);
        }

        private void OnSceneGUI()
        {
            CavePreview preview = (CavePreview)target;
            if (!preview.enabled)
            {
                return;
            }

            CaveRig rig = preview.Rig;
            if (rig == null)
            {
                return;
            }

            EnsureStyle();

            Transform root = preview.transform;
            Vector3 eyeWorld = rig.EyeWorldPosition;

            for (int i = 0; i < CaveRig.FaceCount; i++)
            {
                CaveFace face = (CaveFace)i;
                CaveFaceRect rect = rig.GetFaceRect(face);

                Vector3 centerWorld = root.TransformPoint(rect.center);
                Vector3 towardEye = (eyeWorld - centerWorld).normalized;
                Vector3 labelPosition = centerWorld + towardEye * 0.05f;

                Handles.Label(labelPosition, BuildLabel(rig, face, rect), labelStyle);
            }

            if (preview.showEyePoint)
            {
                Handles.Label(eyeWorld, "Olho\n" + FormatVector(rig.EyeLocalPosition) + " local", labelStyle);
            }
        }

        private static string BuildLabel(CaveRig rig, CaveFace face, CaveFaceRect rect)
        {
            RenderTexture rt = rig.GetRenderTexture(face);
            CaveCoverage coverage = rig.GetCoverage(face);

            string rtLine = rt != null
                ? string.Format(Culture, "{0}  {1}x{2}  asp {3:F3}",
                    rt.name, rt.width, rt.height, rt.height > 0 ? (float)rt.width / rt.height : 0f)
                : "sem render texture";

            string coverageLine;
            if (!coverage.valid)
            {
                coverageLine = "cobertura nao medivel";
            }
            else
            {
                Vector2 percent = coverage.DeltaPercent;
                bool ok = coverage.WorstPercent <= rig.mismatchTolerancePercent;
                coverageLine = string.Format(Culture, "cobertura {0:+0.00;-0.00;0.00}% / {1:+0.00;-0.00;0.00}%   {2}",
                    percent.x, percent.y, ok ? "OK" : "DIVERGENTE");
            }

            return string.Format(Culture, "{0}\n{1:F3} x {2:F3} u   asp {3:F3}\n{4}\n{5}",
                face, rect.size.x, rect.size.y, rect.Aspect, rtLine, coverageLine);
        }

        private static string FormatVector(Vector3 value)
        {
            return string.Format(Culture, "({0:F3}, {1:F3}, {2:F3})", value.x, value.y, value.z);
        }

        private static void EnsureStyle()
        {
            if (labelStyle != null)
            {
                return;
            }

            labelStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter,
                richText = false
            };

            labelStyle.normal.textColor = Color.white;
        }
    }
}
