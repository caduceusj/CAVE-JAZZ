using UnityEditor;
using UnityEngine;

namespace CaveJazz.Calibration.EditorTools
{
    /// <summary>
    /// Painel de calibracao da CAVE, para trabalhar sem precisar manter o CaveRoot
    /// selecionado na Hierarchy.
    /// </summary>
    public class CaveCalibrationWindow : EditorWindow
    {
        private CaveRig rig;
        private CaveCalibrationSnapshot snapshot;
        private Vector2 scroll;

        [MenuItem("Window/CAVE/Calibracao")]
        public static void Open()
        {
            CaveCalibrationWindow window = GetWindow<CaveCalibrationWindow>("CAVE");
            window.minSize = new Vector2(560f, 320f);
            window.Show();
        }

        private void OnEnable()
        {
            AutoResolve();
        }

        private void OnFocus()
        {
            AutoResolve();
        }

        private void OnInspectorUpdate()
        {
            // A tabela mostra medidas ao vivo; sem repaint periodico ela congela
            // enquanto a pessoa arrasta uma camera no Scene View.
            Repaint();
        }

        private void AutoResolve()
        {
            if (rig == null)
            {
                rig = FindFirstObjectByType<CaveRig>(FindObjectsInactive.Include);
            }

            if (snapshot == null)
            {
                snapshot = FindFirstObjectByType<CaveCalibrationSnapshot>(FindObjectsInactive.Include);
            }
        }

        private void OnGUI()
        {
            using (EditorGUILayout.ScrollViewScope scope = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = scope.scrollPosition;

                EditorGUILayout.LabelField("Alvos", EditorStyles.boldLabel);
                rig = (CaveRig)EditorGUILayout.ObjectField("Cave Rig", rig, typeof(CaveRig), true);
                snapshot = (CaveCalibrationSnapshot)EditorGUILayout.ObjectField(
                    "Snapshot", snapshot, typeof(CaveCalibrationSnapshot), true);

                if (GUILayout.Button("Procurar na cena"))
                {
                    rig = FindFirstObjectByType<CaveRig>(FindObjectsInactive.Include);
                    snapshot = FindFirstObjectByType<CaveCalibrationSnapshot>(FindObjectsInactive.Include);
                }

                EditorGUILayout.Space(10f);
                EditorGUILayout.LabelField("Proporcoes e cobertura", EditorStyles.boldLabel);
                CaveCalibrationGUI.DrawFaceTable(rig);

                if (rig != null)
                {
                    EditorGUILayout.Space(8f);
                    CaveCalibrationGUI.DrawRigActions(rig);
                }

                EditorGUILayout.Space(10f);
                EditorGUILayout.LabelField("Snapshot", EditorStyles.boldLabel);
                CaveCalibrationGUI.DrawSnapshotActions(snapshot);
            }
        }
    }
}
