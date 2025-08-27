using UnityEngine;

// Ensure these components are present on the GameObject
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(BoxCollider2D))] // Using BoxCollider2D as a common default for 2D players
public class PlayerController : MonoBehaviour
{
    [Header("Movement Parameters")]
    public float moveSpeed = 5f;
    public float jumpForce = 10f;

    [Header("Ground Check")]
    [SerializeField] private Transform groundCheck; // Assign an empty GameObject below the player in the Inspector
    [SerializeField] private LayerMask groundLayer; // Assign the "Ground" layer in the Inspector
    [SerializeField] private float groundCheckRadius = 0.2f;

    [Header("Player Stats")]
    public int currentHealth = 3;
    public int maxHealth = 3;
    public int score = 0;

    // Internal references
    private Rigidbody2D rb;
    private bool isGrounded;
    private float horizontalInput;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();

        // Set up Rigidbody2D properties for a typical 2D platformer player
        rb.freezeRotation = true; // Prevent the player from tipping over due to collisions
        rb.gravityScale = 3f;    // A common gravity scale for faster falling
    }

    void Update()
    {
        // Check if the player is grounded by checking for colliders within a small circle below
        isGrounded = Physics2D.OverlapCircle(groundCheck.position, groundCheckRadius, groundLayer);

        // Get horizontal input (A/D keys or Left/Right Arrow keys)
        horizontalInput = Input.GetAxis("Horizontal");

        // Handle jump input (Space key by default)
        if (Input.GetButtonDown("Jump") && isGrounded)
        {
            // Apply upward force for jumping, maintaining current horizontal velocity
            rb.velocity = new Vector2(rb.velocity.x, jumpForce);
        }

        // Simple check for falling out of bounds (example death condition)
        if (transform.position.y < -10f) // If player falls below a certain Y coordinate
        {
            TakeDamage(maxHealth); // Instant death if falling too far
        }

        // Example: You might update UI elements for score/health here or via a GameManager
        // Debug.Log("Current Score: " + score + ", Health: " + currentHealth);
    }

    void FixedUpdate()
    {
        // Apply horizontal movement using Rigidbody2D.velocity for physics-based movement
        // This ensures consistent movement regardless of frame rate
        rb.velocity = new Vector2(horizontalInput * moveSpeed, rb.velocity.y);
    }

    // Handles trigger collisions with other 2D colliders
    void OnTriggerEnter2D(Collider2D other)
    {
        // Check for collectibles (ensure collectible GameObjects have the "Collectible" tag)
        if (other.CompareTag("Collectible"))
        {
            CollectItem(other.gameObject);
        }
        // Check for enemies (ensure enemy GameObjects have the "Enemy" tag)
        else if (other.CompareTag("Enemy"))
        {
            TakeDamage(1); // Player takes 1 damage when colliding with an enemy
        }
    }

    // Method to handle collecting items
    private void CollectItem(GameObject collectibleObject)
    {
        Debug.Log("Collected: " + collectibleObject.name);
        score += 10; // Increase score by 10
        Destroy(collectibleObject); // Remove the collected item from the scene
        // Potentially trigger a UI update, sound effect, or special ability here
    }

    // Method to handle taking damage
    public void TakeDamage(int amount)
    {
        if (currentHealth <= 0) return; // Prevent taking damage if already dead

        currentHealth -= amount;
        Debug.Log("Player took " + amount + " damage. Current Health: " + currentHealth);

        // Implement visual feedback (e.g., player flashes red, plays a hurt sound)
        // You might call a method on a separate PlayerVisuals script or a GameManager here.

        if (currentHealth <= 0)
        {
            Die();
        }
    }

    // Method to handle player death
    private void Die()
    {
        Debug.Log("Player has died!");
        // Implement death animation, show a game over screen, restart the level, etc.
        // For now, we'll simply disable the player GameObject.
        gameObject.SetActive(false);
        // You might call a GameManager method to handle overall game state here:
        // GameManager.Instance.GameOver();
    }

    // Draw a gizmo in the editor to visualize the ground check radius
    private void OnDrawGizmosSelected()
    {
        if (groundCheck != null)
        {
            Gizmos.color = Color.red;
            // Draw a wire sphere at the groundCheck position with the specified radius
            Gizmos.DrawWireSphere(groundCheck.position, groundCheckRadius);
        }
    }
}