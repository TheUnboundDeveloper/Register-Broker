# Register-Broker Code of Conduct

## Our Commitment

The Register-Broker project is committed to providing a respectful, professional, and secure collaboration environment for everyone who participates in the project.

Register-Broker deals with sensitive areas of system interaction, including brokered access, hardware interfaces, driver-adjacent workflows, permissions, and security boundaries. Because of that, contributors are expected to act with integrity, care, and respect for both the project and the people involved.

We welcome constructive participation from developers, testers, security researchers, documentation contributors, and users.

## Expected Behavior

Participants are expected to:

- Treat others with respect, professionalism, and patience.
- Provide constructive feedback focused on ideas, code, design, and security impact rather than individuals.
- Assume good intent, but verify technical claims through evidence, testing, and review.
- Respect project boundaries around safety, security, and responsible disclosure.
- Communicate clearly when proposing changes that affect permissions, privilege boundaries, drivers, hardware access, authentication, logging, or data handling.
- Help maintain a project environment where people can ask questions, report issues, and contribute without being dismissed or attacked.
- Acknowledge mistakes, correct misinformation, and update designs when better evidence or safer approaches are identified.

## Unacceptable Behavior

The following behavior is not acceptable:

- Harassment, intimidation, personal attacks, insults, or abusive language.
- Discriminatory comments or conduct.
- Trolling, hostile arguments, or repeated disruption of project discussions.
- Publishing or threatening to publish someone else’s private information.
- Deliberately submitting malicious code, hidden behavior, unsafe bypasses, credential harvesting logic, or intentionally deceptive changes.
- Encouraging users to disable security protections without clear justification, risk disclosure, and safer alternatives.
- Attempting to use the project to bypass operating system security, gain unauthorized access, evade monitoring, or interfere with systems without permission.
- Publicly disclosing security vulnerabilities before maintainers have had a reasonable opportunity to investigate and respond.

## Security and Safety Expectations

Register-Broker is intended to promote safer brokered access patterns and reduce unnecessary privilege exposure. Contributors should keep that purpose in mind.

Contributions that affect security-sensitive behavior should be reviewed carefully. This includes changes involving:

- Driver communication
- Named pipes, sockets, ALPC, or other IPC mechanisms
- Authentication or session handling
- Access control lists and permission checks
- Hardware register, SMBus, sensor, RGB, or low-level device access
- Logging, telemetry, or audit behavior
- Input validation and command sanitization
- Rate limiting and abuse prevention
- Configuration defaults that may affect system safety

Security-related discussions should remain professional, evidence-based, and focused on reducing risk.

## Responsible Disclosure

If you believe you have found a security vulnerability, please do not open a public issue with exploit details.

Instead, report the issue privately to the project maintainer using the preferred security contact listed in the repository. Include as much detail as possible, such as:

- Affected version or commit
- Description of the issue
- Steps to reproduce
- Potential impact
- Suggested mitigation, if known

The maintainers will review the report, investigate the issue, and coordinate an appropriate fix or disclosure plan.

## Scope

This Code of Conduct applies to all project spaces, including:

- GitHub issues
- Pull requests
- Discussions
- Documentation contributions
- Security reports
- Project-related communication channels
- Any public or private interaction made on behalf of the project

It also applies when someone is representing the project in other communities or technical discussions.

## Enforcement

Project maintainers are responsible for clarifying and enforcing this Code of Conduct. Maintainers may take appropriate action in response to unacceptable behavior, including:

- Issuing a private or public warning
- Requesting edits to comments, issues, or pull requests
- Closing issues or pull requests
- Restricting participation in project spaces
- Temporarily or permanently banning a participant
- Reporting serious abuse or malicious activity to the relevant platform or authority

Enforcement decisions should be fair, consistent, and based on the impact of the behavior on the project and its participants.

## Reporting Issues

If you experience or witness behavior that violates this Code of Conduct, report it to the project maintainer.

When reporting, include:

- A description of what happened
- Links, screenshots, or references where applicable
- The names or usernames involved
- Any relevant context that may help with review

Reports will be handled with reasonable confidentiality and care.

## Good-Faith Security Research

Good-faith security research is welcome when performed responsibly. Researchers should avoid actions that:

- Damage systems
- Access data that does not belong to them
- Disrupt services
- Publish exploit details prematurely
- Encourage unsafe use of the project

Reports made in good faith will be treated respectfully, even when the final assessment determines that the issue is not exploitable or is outside the project’s scope.

## Project Values

Register-Broker values:

- Safety over convenience
- Least privilege over broad access
- Clear boundaries over hidden behavior
- Auditability over opacity
- Responsible disclosure over public exploitation
- Practical engineering over unnecessary complexity
- Respectful collaboration over ego-driven debate

## Attribution

This Code of Conduct is inspired by common open-source community standards and adapted for a security-conscious software project involving brokered system access.
