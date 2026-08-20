using UnityEditor;
using UnityEngine;

namespace CaveJazz.Calibration.EditorTools
{
    [CustomEditor(typeof(CaveCalibrationSnapshot))]
    public class CaveCalibrationSnapshotEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            CaveCalibrationSnapshot snapshot = (CaveCalibrationSnapshot)target;

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Snapshot", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Pasta", snapshot.ResolveOutputFolder(), EditorStyles.miniLabel);

            CaveCalibrationGUI.DrawSnapshotActions(snapshot);

            CaveRig rig = snapshot.ResolveRig();
            if (rig == null)
            {
                return;
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Proporcoes e cobertura", EditorStyles.boldLabel);
            CaveCalibrationGUI.DrawFaceTable(rig);
        }
    }
}
