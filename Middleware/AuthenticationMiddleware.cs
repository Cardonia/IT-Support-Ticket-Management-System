
using Npgsql;
using System.Security.Cryptography;
using System.Text;

public static class AuthenticationMiddleware
{
    public static void UseAuthenticationMiddleware(
        this WebApplication app)
    {
        var connectionString =
            "Host=localhost;Port=5432;Database=it_db;Username=admin;Password=123";

        app.Use(async (context, next) =>
        {
            Console.WriteLine("user url request: " + context.Request.Path);

            var path = context.Request.Path;

            // GET SESSION FROM BROWSER
            var session = context.Request.Cookies["session"];
            bool validSession = false;

            // CHECK SESSION
            if (!string.IsNullOrEmpty(session))
            {
                string sessionHash = Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(session)
                    )
                );

                using var connection =
                    new NpgsqlConnection(connectionString);

                connection.Open();

                string sql = """
                    SELECT COUNT(*)
                    FROM users
                    WHERE session = @session
                    """;

                using var command =
                    new NpgsqlCommand(sql, connection);

                command.Parameters.AddWithValue(
                    "session",
                    sessionHash
                );

                int count =
                    Convert.ToInt32(
                        command.ExecuteScalar()
                    );

                if (count > 0)
                {
                    validSession = true;
                    
                }
            }

            Console.WriteLine("auth: "+validSession);

            // ROOT PAGE
            if (path == "/" || path == "/index.html")
            {
                if (validSession)
                {
                    context.Response.Redirect("/home.html");
                    Console.WriteLine("/ : user session exit and redirect to /home");
                    return;
                }

                await next();
                return;
            }

            // LOGIN / REGISTER PAGES
            if (path == "/login.html" ||
                path == "/register.html")
            {
                if (validSession)
                {
                    context.Response.Redirect("/home.html");
                    Console.WriteLine("login/register : user session exit and redirect to /home");
                    return;
                }

                await next();
                return;
            }


            // --------------------------------
            // LOGIN / REGISTER API
            // --------------------------------

            if (path.StartsWithSegments("/api/login") ||
                path.StartsWithSegments("/api/register"))
            {
                await next();
                return;
            }


            // --------------------------------
            // PUBLIC FILES
            // --------------------------------

            if (path == "/style.css" ||
                path == "/script.js" ||
                path == "/index.html")
            {
                await next();
                return;
            }


            // --------------------------------
            // PROTECTED PAGES
            // --------------------------------

            if (!validSession)
            {
                context.Response.Redirect("/");
                return;
            }

            // Valid session → allow request
            await next();
        });
    }
}
