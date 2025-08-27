using UnityEngine;
using UnityEngine.SceneManagement;

public static class GameUIActions
{
    public static void RestartGame()
    {
        var scene = SceneManager.GetActiveScene();
        SceneManager.LoadScene(scene.name);
    }
}


