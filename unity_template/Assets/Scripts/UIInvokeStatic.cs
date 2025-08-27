using System;
using System.Reflection;
using UnityEngine;

public class UIInvokeStatic : MonoBehaviour
{
    [Tooltip("Static method name on GameUIActions to invoke, e.g. RestartGame")] 
    public string methodName;

    public void InvokeAction()
    {
        if (string.IsNullOrEmpty(methodName))
        {
            Debug.LogWarning("[GameGen] UIInvokeStatic: methodName is empty");
            return;
        }

        var type = FindTypeByName("GameUIActions");
        if (type == null)
        {
            Debug.LogWarning("[GameGen] UIInvokeStatic: GameUIActions type not found");
            return;
        }

        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        if (method == null)
        {
            Debug.LogWarning("[GameGen] UIInvokeStatic: method not found: " + methodName);
            return;
        }

        method.Invoke(null, null);
    }

    private static Type FindTypeByName(string typeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(typeName);
            if (t != null) return t;
        }
        return null;
    }
}


