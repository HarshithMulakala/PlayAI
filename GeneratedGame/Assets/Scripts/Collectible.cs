using UnityEngine;

public class Collectible : MonoBehaviour
{
    [SerializeField] private int scoreValue = 1;

    void Awake()
    {
        CircleCollider2D collider = GetComponent<CircleCollider2D>();
        if (collider == null)
        {
            collider = gameObject.AddComponent<CircleCollider2D>();
        }
        collider.isTrigger = true;

        Rigidbody2D rb = GetComponent<Rigidbody2D>();
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody2D>();
        }
        rb.isKinematic = true;

        if (GetComponent<SpriteRenderer>() == null)
        {
            Debug.LogWarning("Collectible: No SpriteRenderer found. Please add one and assign a sprite.");
        }
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (other.CompareTag("Player"))
        {
            GameManager gameManager = FindObjectOfType<GameManager>();
            if (gameManager != null)
            {
                gameManager.AddScore(scoreValue);
            }
            else
            {
                Debug.LogWarning("Collectible: GameManager not found in the scene to update score. Score will not be updated.");
            }

            Destroy(gameObject);
        }
    }
}