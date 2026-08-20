using UnityEditor;
using UnityEngine;

namespace CaveJazz.Calibration.EditorTools
{
    [CustomEditor(typeof(CaveRig))]
    public class CaveRigEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            CaveRig rig = (CaveRig)target;

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Proporcoes e cobertura", EditorStyles.boldLabel);
            CaveCalibrationGUI.DrawFaceTable(rig);

            EditorGUILayout.Space(8f);
            CaveCalibrationGUI.DrawRigActions(rig);
        }
    }
}
