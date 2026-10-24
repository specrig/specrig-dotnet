Feature: Orders

  Scenario: The customer sees what is left to pay
    Given order 7 is placed
    And billing holds an invoice of 120 for order 7
    When the customer opens order 7
    Then they see 120 to pay

  Scenario: An order the shop does not have is refused
    When the customer opens order 8
    Then they are told there is no such order

  Scenario: The customer's app shows the order and whose it is
    Given order 7 is placed
    And billing holds an invoice of 120 for order 7
    And customers knows order 7 as Ann's
    When the app asks for order 7
    Then it shows 120 to pay for Ann

  Scenario: The customer's app is told the shop has no such order
    When the app asks for order 8
    Then it is told there is no such order

  Scenario: The customer is told to come back while the shop's database is down
    Given order 7 is placed
    And the shop's database is down
    When the customer opens order 7
    Then they are told to come back later
