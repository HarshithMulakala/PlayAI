using UnityEngine;

public class Collectible : MonoBehaviour
{
    public int value = 1;

    private void Reset()
    {
        var col = GetComponent<Collider2D>();
        if (col == null) col = gameObject.AddComponent<CircleCollider2D>();
        col.isTrigger = true;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other.gameObject.GetComponent<PlayerController>() != null)
        {
            Destroy(gameObject);
        }
    }
}


