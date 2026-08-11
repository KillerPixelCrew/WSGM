//! The launch payload the elevated parent hands the medium child over the pipe:
//! working directory, the full game command, and Steam's environment (Task
//! Scheduler gives the child a clean environment, so the child must rebuild it).

use std::path::Path;

use crate::pipe::Pipe;

const VERSION: u32 = 1;

pub struct Payload {
    pub working_directory: String,
    pub arguments: Vec<String>,
    pub environment: Vec<(String, String)>,
}

impl Payload {
    pub fn capture(arguments: &[String]) -> Payload {
        let working_directory = std::env::current_dir()
            .map(|path| path.to_string_lossy().into_owned())
            .unwrap_or_default();
        let environment = std::env::vars().collect();
        Payload {
            working_directory,
            arguments: arguments.to_vec(),
            environment,
        }
    }

    /// The working directory to launch in: the captured one when it still exists,
    /// else the target executable's own directory.
    pub fn resolved_working_directory(&self) -> String {
        if !self.working_directory.is_empty() && Path::new(&self.working_directory).is_dir() {
            return self.working_directory.clone();
        }
        Path::new(&self.arguments[0])
            .parent()
            .map(|parent| parent.to_string_lossy().into_owned())
            .filter(|dir| !dir.is_empty())
            .unwrap_or_else(|| ".".to_string())
    }

    pub fn write_to(&self, pipe: &Pipe) -> Result<(), String> {
        pipe.write_u32(VERSION)?;
        pipe.write_string(&self.working_directory)?;
        pipe.write_u32(self.arguments.len() as u32)?;
        for argument in &self.arguments {
            pipe.write_string(argument)?;
        }
        pipe.write_u32(self.environment.len() as u32)?;
        for (key, value) in &self.environment {
            pipe.write_string(key)?;
            pipe.write_string(value)?;
        }
        Ok(())
    }

    pub fn read_from(pipe: &Pipe) -> Result<Payload, String> {
        let version = pipe.read_u32()?;
        if version != VERSION {
            return Err(format!("unsupported payload version {version}"));
        }
        let working_directory = pipe.read_string()?;

        let argument_count = pipe.read_count()?;
        let mut arguments = Vec::with_capacity(argument_count as usize);
        for _ in 0..argument_count {
            arguments.push(pipe.read_string()?);
        }
        if arguments.is_empty() {
            return Err("payload has no target command".to_string());
        }

        let environment_count = pipe.read_count()?;
        let mut environment = Vec::with_capacity(environment_count as usize);
        for _ in 0..environment_count {
            let key = pipe.read_string()?;
            let value = pipe.read_string()?;
            environment.push((key, value));
        }

        Ok(Payload {
            working_directory,
            arguments,
            environment,
        })
    }
}
