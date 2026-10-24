// The prefix must name a disposable test database. Each suite gets its own database.
export function databaseEnvironmentFor(suite) {
  const connection = process.env.WEBPOSTMAN_TEST_MYSQL_CONNECTION
  if (!connection) return { Database__Provider: 'Sqlite' }
  const match = /(?:^|;)\s*Database\s*=\s*(webpostman_test[a-zA-Z0-9_]*)\s*(?=;|$)/i.exec(connection)
  if (!match) throw new Error('MySQL tests require a disposable Database=webpostman_test... connection')
  return {
    Database__Provider: 'MySql',
    Database__ImportLegacyData: 'true',
    Database__MySqlVersion: process.env.WEBPOSTMAN_TEST_MYSQL_VERSION ?? '8.0.36',
    ConnectionStrings__WebPostman: connection.replace(match[0], match[0].replace(match[1], match[1] + '_' + suite)),
  }
}
