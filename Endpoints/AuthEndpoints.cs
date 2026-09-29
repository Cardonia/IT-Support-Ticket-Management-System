
using Npgsql;
using System.Security.Cryptography;
using System.Text;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/register", (RegisterRequest data, HttpResponse response) =>
        {
            if (data.username.Length <= 3 || data.username.Length >= 15)
            {
                return Results.BadRequest("Username must be 4-14 characters.");
            }

            if (data.password.Length <= 7 || data.password.Length >= 16)
            {
                return Results.BadRequest("Password must be 8-15 characters.");
            }

            var connectionString =
                "Host=localhost;Port=5432;Database=it_db;Username=admin;Password=123";

            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            // Hash password with BCrypt
            string passwordHash =
                BCrypt.Net.BCrypt.HashPassword(data.password);

            // Generate random session token
            byte[] randomBytes = RandomNumberGenerator.GetBytes(32);

            string session = Convert.ToBase64String(randomBytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");

            // Hash session before storing
            string sessionHash = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(session)
                )
            );

            // Insert user
            string sql = """
                INSERT INTO users (first_name, password, role, session)
                VALUES (@first_name, @password, 'guest', @session)
                """;

            using var command = new NpgsqlCommand(sql, connection);

            command.Parameters.AddWithValue(
                "first_name",
                data.username
            );

            command.Parameters.AddWithValue(
                "password",
                passwordHash
            );

            command.Parameters.AddWithValue(
                "session",
                sessionHash
            );

            try
            {
                command.ExecuteNonQuery();
            }
            catch (PostgresException ex)
                when (ex.SqlState == "23505")
            {
                return Results.BadRequest(
                    "Username is already registered."
                );
            }

            // Send original session token to browser
            response.Cookies.Append(
                "session",
                session,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = false,
                    SameSite = SameSiteMode.Strict
                }
            );

            return Results.Ok(
                "User registered successfully."
            );
        });
    }
}
