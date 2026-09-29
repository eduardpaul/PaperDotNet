from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .json_object import JsonObject

@dataclass
class ActivityDescriptor(AdditionalDataHolder, Parsable):
    """
    An activity in the catalog: `kind` is `flow` (run by the engine) or `action`.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The description property
    description: Optional[str] = None
    # The inputSchema property
    input_schema: Optional[JsonObject] = None
    # The key property
    key: Optional[str] = None
    # The kind property
    kind: Optional[str] = None
    # The outputSchema property
    output_schema: Optional[JsonObject] = None
    # The ports property
    ports: Optional[list[str]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ActivityDescriptor:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ActivityDescriptor
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ActivityDescriptor()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .json_object import JsonObject

        from .json_object import JsonObject

        fields: dict[str, Callable[[Any], None]] = {
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "inputSchema": lambda n : setattr(self, 'input_schema', n.get_object_value(JsonObject)),
            "key": lambda n : setattr(self, 'key', n.get_str_value()),
            "kind": lambda n : setattr(self, 'kind', n.get_str_value()),
            "outputSchema": lambda n : setattr(self, 'output_schema', n.get_object_value(JsonObject)),
            "ports": lambda n : setattr(self, 'ports', n.get_collection_of_primitive_values(str)),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_str_value("description", self.description)
        writer.write_object_value("inputSchema", self.input_schema)
        writer.write_str_value("key", self.key)
        writer.write_str_value("kind", self.kind)
        writer.write_object_value("outputSchema", self.output_schema)
        writer.write_collection_of_primitive_values("ports", self.ports)
        writer.write_additional_data_value(self.additional_data)
    

