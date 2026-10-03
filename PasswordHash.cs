// Password checking in one place.
//
// BCrypt.Net throws when the stored hash is not a bcrypt hash (an empty value, a placeholder such
// as 'x', a hash cut short by a copy-and-paste mistake in the database terminal). Without this
// helper that exception would turn a login or a password confirmation into a 500 error.
// A stored value that is not a valid hash simply never matches, and the answer takes about as long as a
// real check, so the response time does not show which accounts have a broken hash.
public static class PasswordHash
{
    // A valid bcrypt hash (work factor 11, same as HashPassword) of a throw-away text; it is only used to spend
    // the same time, its result is never used. A constant, so the first broken account is not slower than the next.
    const string Dummy = "$2a$11$7ur5bh1ulnkqruv1GDnJt.FiEkTD4cr2cZS9LBtDWTT/JqSzByb8K";

    public static bool Verify(string password, string? hash)
    {
        try
        {
            if (!string.IsNullOrEmpty(hash))
                return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException
                                   || ex.GetType().Name is "SaltParseException" or "HashInformationException")
        {
            // not a bcrypt hash: fall through to the equal-cost check below
        }
        BCrypt.Net.BCrypt.Verify(password, Dummy);
        return false;
    }
}
