using UnityEngine;

/// <summary>
/// Controls the behavior of a bug enemy in a Unity 2D game.
/// The bug patrols horizontally within a defined range.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
public class BugController : MonoBehaviour
{
    [Header("Movement")]
    [Tooltip("Speed at which the bug moves.")]
    [SerializeField] private float moveSpeed = 1.5f;
    
    [Tooltip("The horizontal distance the bug will patrol from its initial spawn point in each direction. " +
             "Total patrol width will be 2 * Patrol Range.")]
    [SerializeField] private float patrolRange = 3f;

    private Rigidbody2D rb;
    private SpriteRenderer spriteRenderer;
    private Vector2 initialSpawnPosition; // The world position where the bug initially spawned
    private float leftPatrolLimit;        // The leftmost point the bug will patrol to
    private float rightPatrolLimit;       // The rightmost point the bug will patrol to
    private int currentDirection = 1;     // 1 for right, -1 for left

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        spriteRenderer = GetComponent<SpriteRenderer>();

        // Configure Rigidbody2D for 2D movement and prevent unwanted physics behavior
        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.gravityScale = 0f; // Bugs might float or not be affected by gravity
        rb.freezeRotation = true; // Prevent the bug from rotating inadvertently

        // Store the initial position and calculate patrol limits
        initialSpawnPosition = transform.position;
        leftPatrolLimit = initialSpawnPosition.x - patrolRange;
        rightPatrolLimit = initialSpawnPosition.x + patrolRange;

        // Ensure the Collider2D is not set as a trigger by default for solid collisions
        // (User can override this in Inspector if needed)
        Collider2D collider = GetComponent<Collider2D>();
        if (collider != null)
        {
            collider.isTrigger = false;
        }
    }

    void FixedUpdate()
    {
        PatrolMovement();
    }

    /// <summary>
    /// Handles the horizontal patrolling movement of the bug.
    /// </summary>
    private void PatrolMovement()
    {
        // Set the horizontal velocity based on current direction and speed
        rb.velocity = new Vector2(currentDirection * moveSpeed, rb.velocity.y);

        // Check if the bug has reached its patrol limit in the current direction
        if (currentDirection == 1 && transform.position.x >= rightPatrolLimit)
        {
            ChangeDirection();
        }
        else if (currentDirection == -1 && transform.position.x <= leftPatrolLimit)
        {
            ChangeDirection();
        }
    }

    /// <summary>
    /// Reverses the bug's movement direction and flips its sprite.
    /// </summary>
    private void ChangeDirection()
    {
        currentDirection *= -1; // Reverse direction (1 to -1, or -1 to 1)
        FlipSprite();
    }

    /// <summary>
    /// Flips the bug's sprite horizontally based on its current movement direction.
    /// </summary>
    private void FlipSprite()
    {
        if (spriteRenderer != null)
        {
            // Flip the sprite if moving left (currentDirection == -1)
            // Otherwise, keep it as default (facing right, currentDirection == 1)
            spriteRenderer.flipX = currentDirection == -1;
        }
    }

    /// <summary>
    /// Handles collisions with other 2D objects.
    /// </summary>
    /// <param name="collision">Information about the collision.</param>
    private void OnCollisionEnter2D(Collision2D collision)
    {
        // Example: If the bug collides with a GameObject tagged "Player"
        if (collision.gameObject.CompareTag("Player"))
        {
            Debug.Log($"Bug collided with Player: {collision.gameObject.name}");
            // TODO: Implement interaction logic with player, e.g., deal damage, apply knockback.
            // Example: collision.gameObject.GetComponent<PlayerController>()?.TakeDamage(10);
            
            // It's often good practice for enemies to reverse direction upon hitting the player
            // to prevent them from getting stuck or pushing the player indefinitely.
            ChangeDirection();
        }
        // Example: If the bug hits an environmental obstacle like a wall or ground
        else if (collision.gameObject.layer == LayerMask.NameToLayer("Ground") ||
                 collision.gameObject.layer == LayerMask.NameToLayer("Walls"))
        {
            // Reverse direction to prevent the bug from getting stuck against an obstacle.
            ChangeDirection();
        }
    }

    /// <summary>
    /// Draws visual aids in the Unity editor to help debug the patrol path.
    /// </summary>
    void OnDrawGizmosSelected()
    {
        // Only draw gizmos if a Rigidbody2D is present (i.e., script is initialized or in editor)
        if (rb != null || !Application.isPlaying)
        {
            Vector2 currentEditorPosition = Application.isPlaying ? transform.position : (Vector2)transform.position;
            Vector2 editorInitialPosition = Application.isPlaying ? initialSpawnPosition : (Vector2)transform.position;
            float editorLeftLimit = editorInitialPosition.x - patrolRange;
            float editorRightLimit = editorInitialPosition.x + patrolRange;

            // Draw the patrol path as a yellow line
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(new Vector2(editorLeftLimit, editorInitialPosition.y), new Vector2(editorRightLimit, editorInitialPosition.y));
            
            // Draw spheres at the patrol limits
            Gizmos.DrawSphere(new Vector2(editorLeftLimit, editorInitialPosition.y), 0.15f);
            Gizmos.DrawSphere(new Vector2(editorRightLimit, editorInitialPosition.y), 0.15f);

            // Draw a red arrow indicating the current movement direction
            if (Application.isPlaying)
            {
                Gizmos.color = Color.red;
                Vector3 arrowEnd = currentEditorPosition + (Vector2.right * currentDirection * 0.5f);
                Gizmos.DrawLine(currentEditorPosition, arrowEnd);
                Gizmos.DrawWireSphere(arrowEnd, 0.1f); // Arrowhead indication
            }
        }
    }
}