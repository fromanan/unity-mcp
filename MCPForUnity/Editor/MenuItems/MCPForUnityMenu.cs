using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Setup;
using MCPForUnity.Editor.Windows;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.MenuItems
{
    public static class MCPForUnityMenu
    {
        private const string TopLevelMenuRoot = "MCP";

        [MenuItem(TopLevelMenuRoot + "/Config", priority = 1)]
        [MenuItem(ProductInfo.MenuRoot + "/Toggle MCP Window %#m", priority = 1)]
        public static void ToggleMCPWindow()
        {
            MCPForUnityEditorWindow.ShowWindow();
        }

        [MenuItem(TopLevelMenuRoot + "/Setup", priority = 2)]
        [MenuItem(ProductInfo.MenuRoot + "/Local Setup Window", priority = 2)]
        public static void ShowSetupWindow()
        {
            SetupWindowService.ShowSetupWindow();
        }


        [MenuItem(TopLevelMenuRoot + "/Preferences", priority = 3)]
        [MenuItem(ProductInfo.MenuRoot + "/Edit EditorPrefs", priority = 3)]
        public static void ShowEditorPrefsWindow()
        {
            EditorPrefsWindow.ShowWindow();
        }
    }
}
